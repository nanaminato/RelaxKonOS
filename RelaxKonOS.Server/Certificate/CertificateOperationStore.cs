using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Certificates;

namespace RelaxKonOS.Server.Certificate;

/// <summary>Durable, secret-free operation ledger for certificate lifecycle tasks.</summary>
internal sealed class CertificateOperationStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, PersistedOperation> _operations = [];
    private readonly Dictionary<string, Guid> _byIdempotency = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _certificateGates = new();
    private readonly HostOperationJournal _journal;
    private readonly ICertificateStore _certificates;
    private readonly CertificateRenewalAttemptRepository _renewalAttempts;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    private readonly ILogger<CertificateOperationStore> _logger;

    public CertificateOperationStore(IHostEnvironment environment, HostOperationJournal journal, ICertificateStore certificates,
        CertificateRenewalAttemptRepository renewalAttempts, ILogger<CertificateOperationStore> logger)
    {
        _path = Path.Combine(environment.ContentRootPath, "data", "certificate-operations.json");
        _journal = journal;
        _certificates = certificates;
        _renewalAttempts = renewalAttempts;
        _logger = logger;
        LoadAndRecover();
    }

    public async Task<CertificateOperationDto> StartAsync(string idempotencyKey, Guid certificateId, string kind, string? actor,
        Func<CancellationToken, Task<string>> action, CancellationToken applicationStopping)
    {
        var key = $"{kind}:{certificateId:D}:{idempotencyKey}";
        PersistedOperation operation;
        await _gate.WaitAsync(applicationStopping);
        try
        {
            if (_byIdempotency.TryGetValue(key, out var existing))
            {
                _logger.LogInformation("Certificate operation request reused. OperationId={OperationId} CertificateId={CertificateId} Kind={Kind}",
                    existing, certificateId, kind);
                return _operations[existing].ToDto();
            }
            operation = new PersistedOperation(Guid.NewGuid(), key, certificateId, kind, actor, CertificateOperationState.Queued, "queued", "", null, null);
            _operations.Add(operation.OperationId, operation);
            _byIdempotency.Add(key, operation.OperationId);
            await SaveAsync(applicationStopping);
            _logger.LogInformation("Certificate operation queued. OperationId={OperationId} CertificateId={CertificateId} Kind={Kind} Actor={Actor}",
                operation.OperationId, certificateId, kind, actor);
        }
        finally { _gate.Release(); }
        _ = RunAsync(operation.OperationId, operation.CertificateId, action, applicationStopping);
        return operation.ToDto();
    }

    public async Task<CertificateOperationDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return _operations.GetValueOrDefault(id)?.ToDto(); }
        finally { _gate.Release(); }
    }

    public async Task<CertificateOperationDto?> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_operations.TryGetValue(id, out var operation)) return null;
            if (operation.State is CertificateOperationState.Succeeded or CertificateOperationState.Failed or CertificateOperationState.Cancelled) return operation.ToDto();
            _cancellations.TryGetValue(id, out var source);
            source?.Cancel();
            _operations[id] = operation with { State = CertificateOperationState.Cancelled, Stage = "cancelled", ProblemCode = "certificate.operation_cancelled", CompletedAt = DateTimeOffset.UtcNow };
            await SaveAsync(cancellationToken);
            return _operations[id].ToDto();
        }
        finally { _gate.Release(); }
    }

    private async Task RunAsync(Guid id, Guid certificateId, Func<CancellationToken, Task<string>> action, CancellationToken stopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _cancellations[id] = linked;
        try
        {
            if (!await MarkRunningAsync(id, stopping)) return;
            _logger.LogInformation("Certificate operation started. OperationId={OperationId} CertificateId={CertificateId}", id, certificateId);
            var certificateGate = _certificateGates.GetOrAdd(certificateId, static _ => new SemaphoreSlim(1, 1));
            await certificateGate.WaitAsync(linked.Token);
            string problem;
            try { problem = await action(linked.Token); }
            finally { certificateGate.Release(); }
            await CompleteAsync(id, linked.IsCancellationRequested ? CertificateOperationState.Cancelled : string.IsNullOrEmpty(problem) ? CertificateOperationState.Succeeded : CertificateOperationState.Failed,
                linked.IsCancellationRequested ? "certificate.operation_cancelled" : problem, stopping);
        }
        catch (CertificateOperationException error)
        {
            _logger.LogWarning(error, "Certificate operation failed. OperationId={OperationId} CertificateId={CertificateId} ProblemCode={ProblemCode}",
                id, certificateId, error.ProblemCode);
            await CompleteAsync(id, CertificateOperationState.Failed, error.ProblemCode, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Certificate operation cancelled. OperationId={OperationId} CertificateId={CertificateId}", id, certificateId);
            await CompleteAsync(id, CertificateOperationState.Cancelled, "certificate.operation_cancelled", CancellationToken.None);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Certificate operation failed unexpectedly. OperationId={OperationId} CertificateId={CertificateId}", id, certificateId);
            await CompleteAsync(id, CertificateOperationState.Failed, "certificate.operation_failed", CancellationToken.None);
        }
        finally { _cancellations.TryRemove(id, out _); }
    }

    private async Task<bool> MarkRunningAsync(Guid id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var operation = _operations[id];
            if (operation.State == CertificateOperationState.Cancelled) return false;
            _operations[id] = operation with { State = CertificateOperationState.Running, Stage = "running", StartedAt = DateTimeOffset.UtcNow };
            await SaveAsync(ct);
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task CompleteAsync(Guid id, CertificateOperationState state, string problemCode, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var operation = _operations[id];
            if (operation.State == CertificateOperationState.Cancelled && state != CertificateOperationState.Cancelled) return;
            var completed = operation with { State = state, Stage = state.ToString().ToLowerInvariant(), ProblemCode = problemCode, CompletedAt = DateTimeOffset.UtcNow };
            _operations[id] = completed;
            await SaveAsync(ct);
            _logger.LogInformation("Certificate operation completed. OperationId={OperationId} CertificateId={CertificateId} Kind={Kind} State={State} ProblemCode={ProblemCode}",
                id, completed.CertificateId, completed.Kind, state, problemCode);
            if (completed.Kind == "renew")
            {
                if (state == CertificateOperationState.Failed)
                    await _certificates.UpdateRenewalOutcomeAsync(completed.CertificateId, null, problemCode, ct);
                await _renewalAttempts.RecordAsync(completed.ToDto(), ct);
            }
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
                var recovered = item.State is CertificateOperationState.Queued or CertificateOperationState.Running
                    ? item with { State = CertificateOperationState.Failed, Stage = "interrupted", ProblemCode = "certificate.operation_interrupted", CompletedAt = DateTimeOffset.UtcNow }
                    : item;
                _operations[recovered.OperationId] = recovered;
                _byIdempotency[recovered.IdempotencyKey] = recovered.OperationId;
            }
            SaveAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (JsonException) { }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(_operations.Values, Json), ct);
        File.Move(temp, _path, true);
        await _journal.UpsertAsync(_operations.Values.Select(operation => new CertificateOperationJournalEntry(operation.ToDto(), operation.IdempotencyKey, operation.Actor)), ct);
    }

    private sealed record PersistedOperation(Guid OperationId, string IdempotencyKey, Guid CertificateId, string Kind, string? Actor,
        CertificateOperationState State, string Stage, string ProblemCode, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt)
    {
        public CertificateOperationDto ToDto() => new(OperationId, CertificateId, Kind, State, Stage, ProblemCode, StartedAt, CompletedAt);
    }
}
