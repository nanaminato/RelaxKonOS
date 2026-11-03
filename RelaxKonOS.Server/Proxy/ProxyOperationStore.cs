using System.Text.Json;
using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Server.Proxy;

public delegate Task ProxyOperationStageReporter(string stage);

/// <summary>
/// Durable, host-global operation ledger for typed Proxy mutations.  It queues delegates supplied
/// by domain services only; it is deliberately not a generic command executor.
/// </summary>
public sealed class ProxyOperationStore(IProxyPlatformPaths paths, ILogger<ProxyOperationStore> logger) : IHostedService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ProxyOperationDto> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ProxyOperationDto> _byId = [];
    private bool _loaded;
    private bool _stopping;
    private bool _recoveryBlocked;
    private readonly Dictionary<Guid, TaskCompletionSource> _running = [];
    // Separate retention survives a second failure while writing the failed terminal record.
    private readonly Dictionary<Guid, ProxySubscriptionCleanupException> _cleanupFailures = [];

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken ct)
    {
        Task[] pending;
        await _gate.WaitAsync(ct);
        try { _stopping = true; pending = _running.Values.Select(item => item.Task).ToArray(); }
        finally { _gate.Release(); }
        await Task.WhenAll(pending).WaitAsync(ct);
    }

    public async Task<ProxyOperationDto> EnqueueAsync(string idempotencyKey, string kind, Func<ProxyOperationStageReporter, CancellationToken, Task<string?>> operation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
            throw new ProxyOperationValidationException(ProxyProblemCodes.IdempotencyKeyRequired);
        await _gate.WaitAsync(cancellationToken);
        ProxyOperationDto item;
        try
        {
            await LoadAsync(cancellationToken);
            if (_byKey.TryGetValue(idempotencyKey, out item!)) return item;
            if (_stopping || _recoveryBlocked) throw new ProxyOperationValidationException(ProxyProblemCodes.OperationInterrupted);
            item = new ProxyOperationDto(Guid.NewGuid(), kind, ProxyOperationState.Queued, "queued", "", null, null);
            _byKey.Add(idempotencyKey, item); _byId.Add(item.OperationId, item);
            try { await SaveAsync(cancellationToken); }
            catch { _byKey.Remove(idempotencyKey); _byId.Remove(item.OperationId); throw; }
            _running.Add(item.OperationId, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        finally { _gate.Release(); }

        logger.LogInformation("Proxy operation {OperationId} queued: {OperationKind}.", item.OperationId, kind);
        _ = ExecuteTrackedAsync(item.OperationId, operation);
        return item;
    }

    private async Task ExecuteTrackedAsync(Guid id, Func<ProxyOperationStageReporter, CancellationToken, Task<string?>> operation)
    {
        Exception? failure = null;
        try { await ExecuteAsync(id, operation); }
        catch (Exception exception)
        {
            failure = exception;
            logger.LogError(exception, "Proxy operation did not finish cleanly; recovery ownership is retained. OperationId={OperationId}", id);
        }
        await _gate.WaitAsync();
        try
        {
            var completion = _running[id];
            if (failure is null) { _running.Remove(id); completion.TrySetResult(); }
            else { _recoveryBlocked = true; completion.TrySetException(failure); _ = completion.Task.Exception; }
        }
        finally { _gate.Release(); }
    }

    public async Task<ProxyOperationDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await LoadAsync(cancellationToken); return _byId.GetValueOrDefault(id); }
        finally { _gate.Release(); }
    }

    private async Task ExecuteAsync(Guid id, Func<ProxyOperationStageReporter, CancellationToken, Task<string?>> operation)
    {
        try
        {
            await UpdateAsync(id, item => item with { State = ProxyOperationState.Running, Stage = "running", StartedAt = DateTimeOffset.UtcNow });
            var problem = await operation(async stage =>
            {
                logger.LogDebug("Proxy operation {OperationId} entered stage {Stage}.", id, stage);
                await UpdateAsync(id, item => item with { Stage = stage });
            }, CancellationToken.None);
            if (!string.IsNullOrEmpty(problem))
                logger.LogWarning("Proxy operation {OperationId} failed with {ProblemCode}.", id, problem);
            else
                logger.LogInformation("Proxy operation {OperationId} completed.", id);
            await UpdateAsync(id, item => item with
            {
                State = string.IsNullOrEmpty(problem) ? ProxyOperationState.Succeeded : ProxyOperationState.Failed,
                Stage = string.IsNullOrEmpty(problem) ? "completed" : "failed",
                ProblemCode = problem ?? "", CompletedAt = DateTimeOffset.UtcNow,
            });
        }
        catch (Exception exception)
        {
            if (exception is ProxySubscriptionCleanupException cleanup)
            {
                await _gate.WaitAsync();
                try { _recoveryBlocked = true; _cleanupFailures[id] = cleanup; }
                finally { _gate.Release(); }
            }
            logger.LogWarning(exception, "Proxy operation {OperationId} failed.", id);
            await UpdateAsync(id, item => item with { State = ProxyOperationState.Failed, Stage = "failed", ProblemCode = ProxyProblemCodes.OperationInterrupted, CompletedAt = DateTimeOffset.UtcNow });
            if (exception is ProxySubscriptionCleanupException) throw;
        }
    }

    private async Task UpdateAsync(Guid id, Func<ProxyOperationDto, ProxyOperationDto> update)
    {
        await _gate.WaitAsync();
        try
        {
            await LoadAsync(CancellationToken.None);
            if (!_byId.TryGetValue(id, out var prior)) return;
            var next = update(prior); _byId[id] = next;
            foreach (var key in _byKey.Where(pair => pair.Value.OperationId == id).Select(pair => pair.Key).ToArray()) _byKey[key] = next;
            await SaveAsync(CancellationToken.None);
        }
        finally { _gate.Release(); }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;
        _loaded = true; var path = Path.Combine(paths.GetStateDirectory(), "proxy-operations.json");
        if (!File.Exists(path)) return;
        try
        {
            await using var stream = File.OpenRead(path);
            var entries = await JsonSerializer.DeserializeAsync<List<Entry>>(stream, cancellationToken: cancellationToken) ?? [];
            foreach (var entry in entries)
            {
                var restored = entry.Operation.State is ProxyOperationState.Queued or ProxyOperationState.Running
                    ? entry.Operation with { State = ProxyOperationState.Interrupted, Stage = "interrupted", ProblemCode = ProxyProblemCodes.OperationInterrupted, CompletedAt = DateTimeOffset.UtcNow }
                    : entry.Operation;
                _byKey[entry.IdempotencyKey] = restored; _byId[restored.OperationId] = restored;
            }
        }
        catch (JsonException) { logger.LogWarning("Ignoring corrupt Proxy operation ledger."); }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var dir = paths.GetStateDirectory(); Directory.CreateDirectory(dir); SetPrivateDirectory(dir);
        var path = Path.Combine(dir, "proxy-operations.json"); var temporary = path + ".new";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, _byKey.Select(pair => new Entry(pair.Key, pair.Value)).ToArray(), cancellationToken: cancellationToken);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record Entry(string IdempotencyKey, ProxyOperationDto Operation);

    private static void SetPrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

public sealed class ProxyOperationValidationException(string problemCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
}
