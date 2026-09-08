using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Server.Certificate;

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
internal sealed class WebServerOperationStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, PersistedOperation> _operations = [];
    private readonly Dictionary<string, Guid> _byIdempotency = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _instanceGates = new(StringComparer.Ordinal);
    private readonly HostOperationJournal _journal;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    public WebServerOperationStore(IHostEnvironment environment, HostOperationJournal journal)
    {
        _path = Path.Combine(environment.ContentRootPath, "data", "webserver-operations.json");
        _journal = journal;
        LoadAndRecover();
    }

    public async Task<WebServerOperationDto> StartAsync(string idempotencyKey, string instanceId, string kind, string? actor,
        Func<CancellationToken, Task<WebServerOperationResult>> action, CancellationToken applicationStopping)
        => await StartAsync(idempotencyKey, instanceId, kind, actor, (_, cancellationToken) => action(cancellationToken), applicationStopping);

    public async Task<WebServerOperationDto> StartAsync(string idempotencyKey, string instanceId, string kind, string? actor,
        Func<IWebServerOperationProgress, CancellationToken, Task<WebServerOperationResult>> action, CancellationToken applicationStopping)
    {
        var key = $"{kind}:{instanceId}:{idempotencyKey}";
        PersistedOperation operation;
        await _gate.WaitAsync(applicationStopping);
        try
        {
            if (_byIdempotency.TryGetValue(key, out var existingId))
                return _operations[existingId].ToDto();

            operation = new PersistedOperation(Guid.NewGuid(), key, instanceId, kind, actor, WebServerOperationState.Queued,
                "queued", "", null, null, null);
            _operations.Add(operation.OperationId, operation);
            _byIdempotency.Add(key, operation.OperationId);
            await SaveAsync(applicationStopping);
        }
        finally { _gate.Release(); }

        _ = RunAsync(operation.OperationId, operation.InstanceId, action, applicationStopping);
        return operation.ToDto();
    }

    public async Task<WebServerOperationDto?> GetAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return _operations.GetValueOrDefault(operationId)?.ToDto(); }
        finally { _gate.Release(); }
    }

    public async Task<WebServerOperationDto?> CancelAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_operations.TryGetValue(operationId, out var operation)) return null;
            if (operation.State is WebServerOperationState.Succeeded or WebServerOperationState.Failed or WebServerOperationState.Cancelled)
                return operation.ToDto();
            _cancellations.TryGetValue(operationId, out var source);
            source?.Cancel();
            operation = operation with { State = WebServerOperationState.Cancelled, Stage = "cancelled", ProblemCode = "webserver.operation_cancelled", CompletedAt = DateTimeOffset.UtcNow };
            _operations[operationId] = operation;
            await SaveAsync(cancellationToken);
            return operation.ToDto();
        }
        finally { _gate.Release(); }
    }

    private async Task RunAsync(Guid operationId, string instanceId, Func<IWebServerOperationProgress, CancellationToken, Task<WebServerOperationResult>> action, CancellationToken applicationStopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
        _cancellations[operationId] = linked;
        try
        {
            if (!await SetRunningAsync(operationId, applicationStopping)) return;
            var instanceGate = _instanceGates.GetOrAdd(instanceId, static _ => new SemaphoreSlim(1, 1));
            await instanceGate.WaitAsync(linked.Token);
            WebServerOperationResult result;
            try { result = await action(new OperationProgress(this, operationId), linked.Token); }
            finally { instanceGate.Release(); }
            await CompleteAsync(operationId, linked.IsCancellationRequested ? WebServerOperationState.Cancelled : string.IsNullOrEmpty(result.ProblemCode) ? WebServerOperationState.Succeeded : WebServerOperationState.Failed,
                linked.IsCancellationRequested ? "webserver.operation_cancelled" : result.ProblemCode, result.SnapshotId, applicationStopping);
        }
        catch (OperationCanceledException) { await CompleteAsync(operationId, WebServerOperationState.Cancelled, "webserver.operation_cancelled", null, CancellationToken.None); }
        catch { await CompleteAsync(operationId, WebServerOperationState.Failed, "webserver.operation_failed", null, CancellationToken.None); }
        finally { _cancellations.TryRemove(operationId, out _); }
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

    private void LoadAndRecover()
    {
        if (!File.Exists(_path)) return;
        try
        {
            foreach (var item in JsonSerializer.Deserialize<PersistedOperation[]>(File.ReadAllText(_path), Json) ?? [])
            {
                var recovered = item.State is WebServerOperationState.Queued or WebServerOperationState.Running
                    ? item with { State = WebServerOperationState.Failed, Stage = "interrupted", ProblemCode = "webserver.operation_interrupted", CompletedAt = DateTimeOffset.UtcNow }
                    : item;
                _operations[recovered.OperationId] = recovered;
                _byIdempotency[recovered.IdempotencyKey] = recovered.OperationId;
            }
            SaveAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (JsonException) { /* A corrupt non-secret ledger must not prevent read-only discovery. */ }
    }

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

    private sealed class OperationProgress(WebServerOperationStore owner, Guid operationId) : IWebServerOperationProgress
    {
        public Task ReportAsync(string stage, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stage);
            return owner.ReportStageAsync(operationId, stage, cancellationToken);
        }
    }
}
