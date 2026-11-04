using System.Text.Json;
using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Server.Proxy;

public delegate Task ProxyOperationStageReporter(string stage);

/// <summary>
/// Durable, host-global operation ledger for typed Proxy mutations.  It queues delegates supplied
/// by domain services only; it is deliberately not a generic command executor.
/// </summary>
public sealed class ProxyOperationStore(IProxyPlatformPaths paths, ILogger<ProxyOperationStore> logger, IProxyWorkLeaseSource workLeases) : IHostedService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new()
        { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    private bool _stopping;
    private bool _recoveryBlocked;
    private readonly Dictionary<Guid, TaskCompletionSource> _running = [];
    // Separate retention survives a second failure while writing the failed terminal record.
    private readonly Dictionary<Guid, ProxyOperationCleanupException> _cleanupFailures = [];

    public async Task<IReadOnlyList<string>> InspectResponsibilitiesAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var facts = new List<string>();
            if (_running.Count != 0) facts.Add("Proxy execution, cleanup or terminal confirmation is still owned by this host.");
            if (_recoveryBlocked || _cleanupFailures.Count != 0) facts.Add("Proxy task cleanup or terminal confirmation requires recovery.");
            facts.AddRange(await InspectPersistedResponsibilitiesAsync(paths, ct));
            return facts;
        }
        finally { _gate.Release(); }
    }

    public static async Task<IReadOnlyList<string>> InspectPersistedResponsibilitiesAsync(IProxyPlatformPaths paths, CancellationToken ct)
    {
        try
        {
            var entries = await ReadEntriesAsync(paths, ct);
            return entries.Where(entry => IsUnresolved(entry.Operation))
                .Select(entry => $"Proxy operation {entry.Operation.OperationId} has unresolved execution or interruption responsibility.").ToArray();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or RelaxKonOS.ManagedRuntimes.RuntimePolicyException)
        { return ["Proxy operation ledger cannot be read or verified; task responsibility is unknown."]; }
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken ct)
    {
        Volatile.Write(ref _stopping, true);
        Task[] pending;
        await _gate.WaitAsync(ct);
        try { pending = _running.Values.Select(item => item.Task).ToArray(); }
        finally { _gate.Release(); }
        await Task.WhenAll(pending).WaitAsync(ct);
    }

    public async Task<ProxyOperationDto> EnqueueAsync(string idempotencyKey, string kind, Func<ProxyOperationStageReporter, CancellationToken, Task<string?>> operation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
            throw new ProxyOperationValidationException(ProxyProblemCodes.IdempotencyKeyRequired);
        await _gate.WaitAsync(cancellationToken);
        ProxyOperationDto item;
        RelaxKonOS.Server.Residency.IModuleWorkLease? work = null;
        try
        {
            var entries = await ReadEntriesAsync(paths, cancellationToken);
            var prior = entries.FirstOrDefault(entry => entry.IdempotencyKey == idempotencyKey);
            if (prior is not null) return Visible(prior.Operation);
            if (Volatile.Read(ref _stopping) || _recoveryBlocked || entries.Any(entry => IsUnresolved(entry.Operation) && !_running.ContainsKey(entry.Operation.OperationId)))
                throw new ProxyOperationValidationException(ProxyProblemCodes.OperationInterrupted);
            work = await workLeases.AcquireAsync(cancellationToken);
            item = new ProxyOperationDto(Guid.NewGuid(), kind, ProxyOperationState.Queued, "queued", "", null, null);
            await SaveAsync([.. entries, new Entry(idempotencyKey, item)], cancellationToken);
            _running.Add(item.OperationId, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        catch { work?.Dispose(); throw; }
        finally { _gate.Release(); }

        logger.LogInformation("Proxy operation {OperationId} queued: {OperationKind}.", item.OperationId, kind);
        _ = ExecuteTrackedAsync(item.OperationId, operation, work!);
        return item;
    }

    private async Task ExecuteTrackedAsync(Guid id, Func<ProxyOperationStageReporter, CancellationToken, Task<string?>> operation, RelaxKonOS.Server.Residency.IModuleWorkLease work)
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
            // Publication/scope cleanup and terminal persistence finish before releasing the work claim.
            if (failure is not null) work.RequireRecovery();
            work.Dispose();
            if (failure is null) { _running.Remove(id); completion.TrySetResult(); }
            else { _recoveryBlocked = true; completion.TrySetException(failure); _ = completion.Task.Exception; }
        }
        finally { _gate.Release(); }
    }

    public async Task<ProxyOperationDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        Entry[]? entries = null;
        try
        {
            entries = await ReadEntriesAsync(paths, cancellationToken);
            var found = Array.Find(entries, entry => entry.Operation.OperationId == id);
            return found is null ? null : Visible(found.Operation);
        }
        finally { entries = null; _gate.Release(); }
    }

    private ProxyOperationDto Visible(ProxyOperationDto operation) =>
        operation.State is ProxyOperationState.Queued or ProxyOperationState.Running && !_running.ContainsKey(operation.OperationId)
            ? operation with { State = ProxyOperationState.Interrupted, Stage = "interrupted", ProblemCode = ProxyProblemCodes.OperationInterrupted, CompletedAt = DateTimeOffset.UtcNow }
            : operation;

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
            if (exception is ProxyOperationCleanupException cleanup)
            {
                await _gate.WaitAsync();
                try { _recoveryBlocked = true; _cleanupFailures[id] = cleanup; }
                finally { _gate.Release(); }
            }
            logger.LogWarning(exception, "Proxy operation {OperationId} failed.", id);
            await UpdateAsync(id, item => item with { State = ProxyOperationState.Failed, Stage = "failed", ProblemCode = ProxyProblemCodes.OperationInterrupted, CompletedAt = DateTimeOffset.UtcNow });
            if (exception is ProxyOperationCleanupException) throw;
        }
    }

    private async Task UpdateAsync(Guid id, Func<ProxyOperationDto, ProxyOperationDto> update)
    {
        await _gate.WaitAsync();
        try
        {
            var entries = await ReadEntriesAsync(paths, CancellationToken.None);
            var index = Array.FindIndex(entries, entry => entry.Operation.OperationId == id);
            if (index < 0) throw new InvalidDataException("Proxy operation responsibility disappeared before terminal confirmation.");
            entries[index] = entries[index] with { Operation = update(entries[index].Operation) };
            await SaveAsync(entries, CancellationToken.None);
        }
        finally { _gate.Release(); }
    }

    private static bool IsUnresolved(ProxyOperationDto operation) =>
        operation.State is ProxyOperationState.Queued or ProxyOperationState.Running or ProxyOperationState.Interrupted
        || operation.State == ProxyOperationState.Failed && operation.ProblemCode == ProxyProblemCodes.OperationInterrupted;

    private static async Task<Entry[]> ReadEntriesAsync(IProxyPlatformPaths paths, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = Path.Combine(paths.GetStateDirectory(), "proxy-operations.json");
        RelaxKonOS.ManagedRuntimes.RuntimeSourceStore.RequireNoLinks(path);
        byte[] bytes;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length is 0 or > 32 * 1024 * 1024) throw new InvalidDataException("Invalid Proxy operation ledger size.");
            bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, ct);
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        using var document = JsonDocument.Parse(bytes);
        RejectDuplicates(document.RootElement);
        var entries = JsonSerializer.Deserialize<Entry[]>(bytes, Json)
            ?? throw new InvalidDataException("Invalid Proxy operation ledger.");
        var keys = new HashSet<string>(StringComparer.Ordinal); var ids = new HashSet<Guid>();
        foreach (var entry in entries)
            if (entry is null || string.IsNullOrWhiteSpace(entry.IdempotencyKey) || entry.IdempotencyKey.Length > 200
                || !keys.Add(entry.IdempotencyKey) || entry.Operation is not { } item || item.OperationId == Guid.Empty
                || !ids.Add(item.OperationId) || !Enum.IsDefined(item.State) || string.IsNullOrEmpty(item.Kind)
                || item.Stage is null || item.ProblemCode is null)
                throw new InvalidDataException("Invalid Proxy operation ledger entry.");
        return entries;
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate Proxy operation ledger property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }

    private async Task SaveAsync(Entry[] entries, CancellationToken cancellationToken)
    {
        var dir = paths.GetStateDirectory(); Directory.CreateDirectory(dir); SetPrivateDirectory(dir);
        var path = Path.Combine(dir, "proxy-operations.json"); var temporary = path + ".new";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, entries, cancellationToken: cancellationToken);
            if (stream.Length > 32 * 1024 * 1024) throw new InvalidDataException("Proxy operation ledger requires retention maintenance.");
        }
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
