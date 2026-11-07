using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Server.Certificate;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.WebServer;

internal sealed record WebServerOperationResult(string ProblemCode, string? SnapshotId = null)
{
    public static readonly WebServerOperationResult Success = new("");
}

/// <summary>Allows a long-running web-server operation to publish a durable, client-visible stage.</summary>
internal interface IWebServerOperationProgress
{
    Task ReportAsync(string stage, CancellationToken cancellationToken);
}

/// <summary>Durable idempotency ledger for host-global web-server changes.
/// It contains only operation metadata and stable problem codes, never config content or command output.</summary>
internal sealed class WebServerOperationStore : IHostedService, IDisposable
{
    internal const string NativeReloadDispatchStage = "native-reload-dispatch";
    internal const string NativeRecoveryRequired = "webserver.native_recovery_required";
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, PersistedOperation> _operations = [];
    private readonly Dictionary<string, Guid> _byIdempotency = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _instanceGates = new(StringComparer.Ordinal);
    private readonly HostOperationJournal _journal;
    private readonly ResidentBackgroundWorkOwner _workOwner;
    private bool _ready;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    private readonly ILogger<WebServerOperationStore> _logger;

    public WebServerOperationStore(IHostEnvironment environment, HostOperationJournal journal, ILogger<WebServerOperationStore> logger, IResidentModuleWorkLeaseSource leases)
    {
        _path = LedgerPath(environment);
        _journal = journal;
        _logger = logger;
        _workOwner = new(leases, ServerModules.WebServerId);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var recovery = await _workOwner.AdmitAsync(cancellationToken);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, recovery.Token);
        await _gate.WaitAsync(stopping.Token);
        try
        {
            if (_ready) return;
            try
            {
                await LoadAndRecoverAsync(stopping.Token);
                stopping.Token.ThrowIfCancellationRequested();
                _ready = true;
            }
            catch (Exception error)
            {
                recovery.Fail(error);
                if (error is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
                _logger.LogError(error, "Web-server operation startup recovery failed; operation admission remains closed.");
            }
        }
        finally { _gate.Release(); }
    }

    private void RequireReady()
    {
        if (!_ready)
            throw new ModuleUnavailableException("module-load-failed", 30, "Web-server operation startup recovery is not complete.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => _workOwner.StopAsync(cancellationToken);
    public void Dispose() => _workOwner.Dispose();

    public async Task<WebServerOperationDto> StartAsync(string idempotencyKey, string instanceId, string kind, string? actor,
        Func<CancellationToken, Task<WebServerOperationResult>> action, CancellationToken applicationStopping)
        => await StartAsync(idempotencyKey, instanceId, kind, actor, (_, cancellationToken) => action(cancellationToken), applicationStopping);

    internal Task<WebServerOperationDto> StartScopedAsync<TExecution>(string idempotencyKey, string instanceId, string kind, string? actor,
        IServiceScopeFactory scopes, Func<TExecution, IWebServerOperationProgress, CancellationToken, Task<WebServerOperationResult>> action, CancellationToken applicationStopping)
        where TExecution : notnull =>
        StartAsync(idempotencyKey, instanceId, kind, actor, async (progress, ct) =>
        {
            var scope = scopes.CreateAsyncScope();
            try { return await action(scope.ServiceProvider.GetRequiredService<TExecution>(), progress, ct); }
            finally
            {
                try { await scope.DisposeAsync(); }
                catch (Exception error) { throw new ExecutionCleanupException(error); }
            }
        }, applicationStopping);

    public async Task<WebServerOperationDto> StartAsync(string idempotencyKey, string instanceId, string kind, string? actor,
        Func<IWebServerOperationProgress, CancellationToken, Task<WebServerOperationResult>> action, CancellationToken applicationStopping)
    {
        var key = $"{kind}:{instanceId}:{idempotencyKey}";
        using var work = await _workOwner.AdmitAsync(applicationStopping);
        await _gate.WaitAsync(applicationStopping);
        try
        {
            RequireReady();
            if (_byIdempotency.TryGetValue(key, out var existingId))
            {
                var existing = _operations[existingId];
                return string.Equals(existing.Actor, actor, StringComparison.Ordinal) ? existing.ToDto()
                    : new(Guid.Empty, instanceId, kind, WebServerOperationState.Failed, "validation",
                        "webserver.idempotency_conflict", null, null, DateTimeOffset.UtcNow);
            }

            if (_operations.Values.Any(operation => operation.InstanceId == instanceId
                && operation.ProblemCode == NativeRecoveryRequired))
                return new(Guid.Empty, instanceId, kind, WebServerOperationState.Failed, "validation",
                    NativeRecoveryRequired, null, null, DateTimeOffset.UtcNow);

            work.Token.ThrowIfCancellationRequested();
            var operation = new PersistedOperation(Guid.NewGuid(), key, instanceId, kind, actor, WebServerOperationState.Queued,
                "queued", "", null, null, null);
            _operations.Add(operation.OperationId, operation);
            _byIdempotency.Add(key, operation.OperationId);
            try { await SaveAsync(CancellationToken.None); }
            catch (Exception error) { work.Fail(error); throw; }
            var cancellation = new CancellationTokenSource();
            _cancellations[operation.OperationId] = cancellation;
            work.Run(stopping => RunAsync(operation.OperationId, instanceId, action, applicationStopping, stopping, cancellation));
            _logger.LogInformation("Web-server operation queued. OperationId={OperationId}, Kind={Kind}, InstanceId={InstanceId}, Actor={Actor}", operation.OperationId, kind, instanceId, actor ?? "<anonymous>");
            return operation.ToDto();
        }
        finally { _gate.Release(); }
    }

    public async Task<WebServerOperationDto?> FindRequestAsync(string idempotencyKey, string instanceId, string kind,
        string? actor, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RequireReady();
            if (!_byIdempotency.TryGetValue($"{kind}:{instanceId}:{idempotencyKey}", out var id)) return null;
            var operation = _operations[id];
            return string.Equals(operation.Actor, actor, StringComparison.Ordinal) ? operation.ToDto() : null;
        }
        finally { _gate.Release(); }
    }

    public async Task<WebServerOperationDto?> GetAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { RequireReady(); return _operations.GetValueOrDefault(operationId)?.ToDto(); }
        finally { _gate.Release(); }
    }

    public async Task<WebServerOperationDto?> CancelAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RequireReady();
            if (!_operations.TryGetValue(operationId, out var operation)) return null;
            if (_cancellations.TryGetValue(operationId, out var source))
                _ = source.CancelAsync();
            return operation.ToDto();
        }
        finally { _gate.Release(); }
    }

    private async Task RunAsync(Guid operationId, string instanceId, Func<IWebServerOperationProgress, CancellationToken, Task<WebServerOperationResult>> action,
        CancellationToken applicationStopping, CancellationToken workStopping, CancellationTokenSource cancellation)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping, workStopping, cancellation.Token);
            var state = WebServerOperationState.Failed;
            var result = new WebServerOperationResult("webserver.operation_failed");
            if (!linked.IsCancellationRequested && !await SetRunningAsync(operationId, CancellationToken.None)) return;
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                var instanceGate = _instanceGates.GetOrAdd(instanceId, static _ => new SemaphoreSlim(1, 1));
                await instanceGate.WaitAsync(linked.Token);
                try
                {
                    result = await action(new OperationProgress(this, operationId), linked.Token);
                    if (result.ProblemCode == NativeRecoveryRequired)
                    {
                        await PreserveUnknownNativeOperationAsync(operationId);
                        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping, workStopping);
                        await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
                    }
                }
                finally { instanceGate.Release(); }
                state = string.IsNullOrEmpty(result.ProblemCode) ? WebServerOperationState.Succeeded : WebServerOperationState.Failed;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            catch (Exception exception) when (exception is not (NginxProcessCleanupException or ExecutionCleanupException or ResidentWorkCompletionException))
            {
                _logger.LogError(exception, "Web-server operation failed unexpectedly. OperationId={OperationId}, InstanceId={InstanceId}", operationId, instanceId);
            }
            if (linked.IsCancellationRequested)
            {
                // Shutdown leaves the original active receipt for startup reconciliation.
                if (applicationStopping.IsCancellationRequested || workStopping.IsCancellationRequested) return;
                state = WebServerOperationState.Cancelled;
                result = new("webserver.operation_cancelled", result.SnapshotId);
            }
            await CompleteAsync(operationId, state, result.ProblemCode, result.SnapshotId, CancellationToken.None);
        }
        finally
        {
            await _gate.WaitAsync();
            try { _cancellations.TryRemove(operationId, out _); cancellation.Dispose(); }
            finally { _gate.Release(); }
        }
    }

    private async Task PreserveUnknownNativeOperationAsync(Guid id)
    {
        await _gate.WaitAsync();
        try
        {
            _operations[id] = MarkNativeUnconfirmed(_operations[id]);
            try { await SaveAsync(CancellationToken.None); }
            catch (Exception error) { throw new ResidentWorkCompletionException(error); }
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> SetRunningAsync(Guid id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var operation = _operations[id];
            if (operation.State == WebServerOperationState.Cancelled) return false;
            _operations[id] = operation with { State = WebServerOperationState.Running, Stage = "running", StartedAt = DateTimeOffset.UtcNow };
            await SaveAsync(ct);
            _logger.LogInformation("Web-server operation started. OperationId={OperationId}, Kind={Kind}, InstanceId={InstanceId}", id, operation.Kind, operation.InstanceId);
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task CompleteAsync(Guid id, WebServerOperationState state, string problemCode, string? snapshotId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var operation = _operations[id];
            if (operation.State == WebServerOperationState.Cancelled && state != WebServerOperationState.Cancelled) return;
            _operations[id] = operation with { State = state, Stage = state.ToString().ToLowerInvariant(), ProblemCode = problemCode, SnapshotId = snapshotId ?? operation.SnapshotId, CompletedAt = DateTimeOffset.UtcNow };
            await SaveAsync(ct);
            _logger.LogInformation("Web-server operation state persisted. OperationId={OperationId}, State={State}, Problem={Problem}, SnapshotId={SnapshotId}", id, state, problemCode, snapshotId ?? operation.SnapshotId);
        }
        finally { _gate.Release(); }
    }

    private async Task ReportStageAsync(Guid id, string stage, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_operations.TryGetValue(id, out var operation)
                || operation.State is WebServerOperationState.Succeeded or WebServerOperationState.Failed or WebServerOperationState.Cancelled)
                return;
            _operations[id] = operation with { Stage = stage };
            await SaveAsync(ct);
        }
        finally { _gate.Release(); }
    }

    private static string LedgerPath(IHostEnvironment environment) =>
        Path.Combine(environment.ContentRootPath, "data", "webserver-operations.json");

    internal static IReadOnlyList<WebServerOperationDto> ReadPending(IHostEnvironment environment) =>
        ReadLedger(LedgerPath(environment))
            .Where(operation => operation.State is WebServerOperationState.Queued or WebServerOperationState.Running)
            .Select(operation => operation.ToDto()).ToArray();

    private static PersistedOperation[] ReadLedger(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<PersistedOperation[]>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("The web-server ledger must contain an operation array.");
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }

    private async Task LoadAndRecoverAsync(CancellationToken ct)
    {
        var items = ReadLedger(_path);
        if (items.Length == 0) return;
        _operations.Clear();
        _byIdempotency.Clear();
        foreach (var item in items)
        {
            var recovered = item switch
            {
                { State: WebServerOperationState.Running, Stage: NativeReloadDispatchStage } => MarkNativeUnconfirmed(item),
                { ProblemCode: NativeRecoveryRequired } => item,
                { State: WebServerOperationState.Queued or WebServerOperationState.Running } =>
                    item with { State = WebServerOperationState.Failed, Stage = "interrupted", ProblemCode = "webserver.operation_interrupted", CompletedAt = DateTimeOffset.UtcNow },
                _ => item,
            };
            _operations.Add(recovered.OperationId, recovered);
            _byIdempotency.Add(recovered.IdempotencyKey, recovered.OperationId);
        }
        await SaveAsync(ct);
    }

    private static PersistedOperation MarkNativeUnconfirmed(PersistedOperation operation) =>
        operation with { Stage = "native-recovery-required", ProblemCode = NativeRecoveryRequired };

    private async Task SaveAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(_operations.Values, Json), ct);
        File.Move(temp, _path, true);
        await _journal.UpsertWebAsync(_operations.Values.Select(operation => new WebServerOperationJournalEntry(operation.ToDto(), operation.IdempotencyKey, operation.Actor)), ct);
    }

    private sealed record PersistedOperation(Guid OperationId, string IdempotencyKey, string InstanceId, string Kind, string? Actor,
        WebServerOperationState State, string Stage, string ProblemCode, string? SnapshotId, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt)
    {
        public WebServerOperationDto ToDto() => new(OperationId, InstanceId, Kind, State, Stage, ProblemCode, SnapshotId, StartedAt, CompletedAt);
    }

    private sealed class ExecutionCleanupException(Exception inner) : InvalidOperationException("Web-server execution scope did not drain.", inner);

    private sealed class OperationProgress(WebServerOperationStore owner, Guid operationId) : IWebServerOperationProgress
    {
        public Task ReportAsync(string stage, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stage);
            return owner.ReportStageAsync(operationId, stage, cancellationToken);
        }
    }
}
