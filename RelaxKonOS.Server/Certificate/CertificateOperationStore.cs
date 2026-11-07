using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Certificate;

/// <summary>Durable, secret-free operation ledger for certificate lifecycle tasks.</summary>
internal sealed class CertificateOperationStore : IHostedService, IDisposable
{
    internal const string AutomaticRenewalActor = "renewal-worker";
    internal const string InterruptedProblem = "certificate.operation_interrupted";
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, PersistedOperation> _operations = [];
    private readonly Dictionary<string, Guid> _byIdempotency = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _certificateGates = new();
    private readonly HostOperationJournal _journal;
    private readonly ICertificateStore _certificates;
    private readonly CertificateRenewalAttemptRepository _renewalAttempts;
    private readonly FileHttp01ChallengeStore? _challengeFiles;
    private readonly ResidentBackgroundWorkOwner _work;
    private bool _ready;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    private readonly ILogger<CertificateOperationStore> _logger;

    public CertificateOperationStore(IHostEnvironment environment, HostOperationJournal journal, ICertificateStore certificates,
        CertificateRenewalAttemptRepository renewalAttempts, ILogger<CertificateOperationStore> logger, IResidentModuleWorkLeaseSource leases,
        FileHttp01ChallengeStore? challengeFiles = null)
    {
        _path = Path.Combine(environment.ContentRootPath, "data", "certificate-operations.json");
        _journal = journal;
        _certificates = certificates;
        _renewalAttempts = renewalAttempts;
        _challengeFiles = challengeFiles;
        _logger = logger;
        _work = new(leases, ServerModules.CertificatesId);
    }

    internal bool HasResponsibility => _work.HasResponsibility;
    public async Task StartAsync(CancellationToken ct)
    {
        using var recovery = await _work.AdmitAsync(ct);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, recovery.Token);
        await _gate.WaitAsync(stopping.Token);
        try
        {
            if (_ready) return;
            stopping.Token.ThrowIfCancellationRequested();
            try { await LoadAndRecoverAsync(stopping.Token); _ready = true; }
            catch (OperationCanceledException error) when (ct.IsCancellationRequested) { recovery.Fail(error); throw; }
            catch (Exception error)
            {
                recovery.Fail(error);
                _logger.LogError(error, "Certificate operation startup recovery is unavailable; certificate work is quarantined.");
            }
        }
        finally { _gate.Release(); }
    }
    public Task StopAsync(CancellationToken ct) => _work.StopAsync(ct);
    public void Dispose() => _work.Dispose();
    private void RequireReady()
    { if (!_ready) throw new ModuleUnavailableException("module-load-failed", 30, "Certificate operation startup recovery is not complete."); }
    internal static IReadOnlyList<Guid> ReadPending(IHostEnvironment environment)
    {
        var path = Path.Combine(environment.ContentRootPath, "data", "certificate-operations.json");
        if (Directory.Exists(path)) throw new IOException("Certificate operation ledger path is not a file.");
        if (!File.Exists(path)) return [];
        return (JsonSerializer.Deserialize<PersistedOperation[]>(File.ReadAllText(path), Json) ?? throw new JsonException("Certificate ledger must be an array."))
            .Where(o => o.State is CertificateOperationState.Queued or CertificateOperationState.Running).Select(o => o.OperationId).ToArray();
    }

    // Creation retries have a freshly proposed certificate ID; it cannot be part of their request identity.
    internal static string RequestScope(string requestKey, Guid certificateId, string kind, string? actor) =>
        JsonSerializer.Serialize(new[] { actor ?? "", kind, kind is "issue" or "create-self-signed" ? "" : certificateId.ToString("D"), requestKey });

    private static string WebsiteRequestScope(Guid websitePublicationId, string? actor) =>
        RequestScope(websitePublicationId.ToString("D"), Guid.Empty, "website-issue", actor);

    public Task<CertificateOperationDto> StartAsync(string idempotencyKey, Guid certificateId, string kind, string? actor,
        Func<CancellationToken, Task<string>> action, CancellationToken applicationStopping, CancellationToken admissionCancellation,
        bool automaticRenewal = false) =>
        StartCoreAsync(idempotencyKey, certificateId, kind, actor, (_, ct) => action(ct), applicationStopping, admissionCancellation, automaticRenewal);

    internal Task<CertificateOperationDto> StartOrderAsync(string idempotencyKey, Guid certificateId, string kind, string? actor,
        Func<Func<AcmeOrderIdentity, Task>, Func<WebRootChallengeFile, Task>, CancellationToken, Task<string>> action, CancellationToken applicationStopping,
        CancellationToken admissionCancellation, bool automaticRenewal = false, Guid? websitePublicationId = null) =>
        StartCoreAsync(idempotencyKey, certificateId, kind, actor,
            (id, ct) => action(identity => RecordOrderAsync(id, identity), file => RecordChallengeAsync(id, file), ct),
            applicationStopping, admissionCancellation, automaticRenewal, websitePublicationId);

    private async Task<CertificateOperationDto> StartCoreAsync(string idempotencyKey, Guid certificateId, string kind, string? actor,
        Func<Guid, CancellationToken, Task<string>> action, CancellationToken applicationStopping, CancellationToken admissionCancellation,
        bool automaticRenewal = false, Guid? websitePublicationId = null)
    {
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping, admissionCancellation);
        var key = websitePublicationId is { } publicationId ? WebsiteRequestScope(publicationId, actor) : RequestScope(idempotencyKey, certificateId, kind, actor);
        await _gate.WaitAsync(admission.Token);
        try { RequireReady(); if (FindExistingRequest(key, certificateId, automaticRenewal) is { } replay) return replay.ToDto(); }
        finally { _gate.Release(); }
        using var work = await _work.AdmitAsync(admission.Token);
        PersistedOperation operation;
        await _gate.WaitAsync(admission.Token);
        try
        {
            RequireReady();
            if (FindExistingRequest(key, certificateId, automaticRenewal) is { } existing)
            {
                _logger.LogInformation("Certificate operation request reused. OperationId={OperationId} CertificateId={CertificateId} Kind={Kind}",
                    existing.OperationId, certificateId, kind);
                return existing.ToDto();
            }
            admission.Token.ThrowIfCancellationRequested();
            work.Token.ThrowIfCancellationRequested();
            operation = new PersistedOperation(Guid.NewGuid(), key, certificateId, kind, actor, CertificateOperationState.Queued, "queued", "", null, null,
                WebsitePublicationId: websitePublicationId);
            _operations.Add(operation.OperationId, operation);
            _byIdempotency.Add(key, operation.OperationId);
            try { await SaveAsync(admission.Token); }
            catch (Exception error) { work.Fail(error); throw; }
            _logger.LogInformation("Certificate operation queued. OperationId={OperationId} CertificateId={CertificateId} Kind={Kind} Actor={Actor}",
                operation.OperationId, certificateId, kind, actor);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping, work.Token);
            _cancellations[operation.OperationId] = cancellation;
            work.Run(_ => RunAsync(operation.OperationId, operation.CertificateId, ct => action(operation.OperationId, ct), cancellation));
        }
        finally { _gate.Release(); }
        return operation.ToDto();
    }

    internal async Task<CertificateOperationDto?> GetWebsiteIssueAsync(Guid websitePublicationId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { RequireReady(); return _operations.Values.SingleOrDefault(operation => operation.WebsitePublicationId == websitePublicationId)?.ToDto(); }
        finally { _gate.Release(); }
    }

    private async Task RecordChallengeAsync(Guid id, WebRootChallengeFile file)
    {
        await _gate.WaitAsync();
        try
        {
            var operation = _operations[id];
            if (operation.State != CertificateOperationState.Running || (operation.Challenges?.Length ?? 0) >= 100
                || _operations.Values.Any(item => item.Challenges?.Any(challenge => challenge.Token == file.Token) == true))
                throw new CertificateOperationException("certificate.challenge_ownership_unconfirmed");
            _operations[id] = operation with { Challenges = [.. operation.Challenges ?? [], file] };
            try { await SaveLedgerAsync(CancellationToken.None); }
            catch (Exception error) { _operations[id] = operation; throw new ResidentWorkCompletionException(error); }
        }
        finally { _gate.Release(); }
    }

    public async Task<CertificateOperationDto?> FindRequestAsync(string requestKey, Guid certificateId, string kind, string? actor,
        CancellationToken cancellationToken, bool automaticRenewal = false, Guid? websitePublicationId = null)
    {
        var key = websitePublicationId is { } publicationId ? WebsiteRequestScope(publicationId, actor) : RequestScope(requestKey, certificateId, kind, actor);
        await _gate.WaitAsync(cancellationToken);
        try { RequireReady(); return FindExistingRequest(key, certificateId, automaticRenewal)?.ToDto(); }
        finally { _gate.Release(); }
    }

    private PersistedOperation? FindExistingRequest(string key, Guid certificateId, bool automaticRenewal)
    {
        if (_byIdempotency.TryGetValue(key, out var id)) return _operations[id];
        if (!automaticRenewal) return null;

        // A later scan can use a new key while a renewal is in flight or its CA outcome is still unknown.
        var renewals = _operations.Values.Where(operation => operation.Kind == "renew" && operation.CertificateId == certificateId);
        if (renewals.FirstOrDefault(operation => operation.State is CertificateOperationState.Queued or CertificateOperationState.Running) is { } active)
            return active;
        var lastKnownOrUnresolved = renewals.Where(operation => operation.State == CertificateOperationState.Succeeded
            || RequiresOrderReconciliation(operation)).MaxBy(operation => operation.CompletedAt);
        return lastKnownOrUnresolved is { } last && RequiresOrderReconciliation(last) ? last : null;
    }

    private static bool RequiresOrderReconciliation(PersistedOperation operation) =>
        operation.ProblemCode == InterruptedProblem || operation.State == CertificateOperationState.Cancelled && operation.Order is not null;

    public async Task<CertificateOperationDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { RequireReady(); return _operations.GetValueOrDefault(id)?.ToDto(); }
        finally { _gate.Release(); }
    }

    internal async Task<IReadOnlyList<CertificateOperationDto>> GetRenewalsForScanAsync(
        string requestKey, IReadOnlyList<Guid> attached, CancellationToken ct)
    {
        var attachedIds = attached.ToHashSet();
        await _gate.WaitAsync(ct);
        try
        {
            RequireReady();
            return _operations.Values.Where(operation => operation.Kind == "renew"
                && (attachedIds.Contains(operation.OperationId) || operation.Actor == AutomaticRenewalActor
                    && operation.IdempotencyKey == RequestScope(requestKey, operation.CertificateId, "renew", AutomaticRenewalActor)))
                .Select(operation => operation.ToDto()).ToArray();
        }
        finally { _gate.Release(); }
    }

    internal async Task<CertificateOrderObservationDto?> ObserveOrderAsync(Guid id, IAcmeService acme, CancellationToken ct)
    {
        PersistedOperation? operation;
        await _gate.WaitAsync(ct);
        try { RequireReady(); operation = _operations.GetValueOrDefault(id); }
        finally { _gate.Release(); }
        if (operation is null) return null;
        if (!RequiresOrderReconciliation(operation) || operation.Order is null)
            return new(operation.ToDto(), "identity-unavailable");

        using var work = await _work.AdmitAsync(ct);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, work.Token);
        var status = await acme.ObserveOrderAsync(operation.Order, stopping.Token);
        return new(operation.ToDto(), status);
    }

    private async Task RecordOrderAsync(Guid id, AcmeOrderIdentity identity)
    {
        await _gate.WaitAsync();
        try
        {
            var operation = _operations[id];
            if (operation.State != CertificateOperationState.Running || operation.Order is not null)
                throw new ResidentWorkCompletionException(new InvalidOperationException("Invalid ACME order checkpoint."));
            _operations[id] = operation with { Order = identity };
            try { await SaveLedgerAsync(CancellationToken.None); }
            catch (Exception error)
            {
                _operations[id] = operation;
                throw new ResidentWorkCompletionException(error);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<CertificateRenewalInfoDto> GetRenewalInfoAsync(Guid certificateId, bool automaticEnabled, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RequireReady();
            var retry = await _renewalAttempts.GetScheduleAsync(certificateId, cancellationToken);
            var attempts = _operations.Values.Where(operation => operation.CertificateId == certificateId && operation.Kind == "renew")
                .Reverse().Take(5).Select(operation => new CertificateRenewalAttemptDto(operation.Actor == AutomaticRenewalActor, operation.ToDto())).ToArray();
            return new(automaticEnabled, retry.ConsecutiveFailures, retry.RetryAfter, retry.Exhausted, attempts);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<CertificateRenewalAttemptDto>> GetRenewalHistoryAsync(Guid certificateId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { RequireReady(); return _operations.Values.Where(o => o.CertificateId == certificateId && o.Kind == "renew")
            .Reverse().Select(o => new CertificateRenewalAttemptDto(o.Actor == AutomaticRenewalActor, o.ToDto())).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<CertificateRenewalRunDto>> GetManualRenewalRunsAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { RequireReady(); return _operations.Values.Where(o => o.Kind == "renew" && o.Actor != AutomaticRenewalActor)
            .Select(o => new CertificateRenewalRunDto(o.OperationId, o.StartedAt ?? o.CompletedAt ?? DateTimeOffset.UtcNow,
                o.CompletedAt, false, o.State == CertificateOperationState.Succeeded ? 1 : 0,
                o.State == CertificateOperationState.Failed ? 1 : 0,
                o.State is CertificateOperationState.Queued or CertificateOperationState.Running ? 1 : 0,
                o.State == CertificateOperationState.Cancelled ? 1 : 0)).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<CertificateOperationDto?> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        CancellationTokenSource? source = null;
        CertificateOperationDto? result;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RequireReady();
            if (!_operations.TryGetValue(id, out var operation)) return null;
            if (operation.State is CertificateOperationState.Succeeded or CertificateOperationState.Failed or CertificateOperationState.Cancelled) return operation.ToDto();
            _cancellations.TryGetValue(id, out source);
            _operations[id] = operation with { Stage = "cancel-requested" };
            try { await SaveAsync(cancellationToken); }
            catch { _operations[id] = operation; throw; }
            result = _operations[id].ToDto();
        }
        finally { _gate.Release(); }
        try { source?.Cancel(); } catch (ObjectDisposedException) { /* The actual task already completed. */ }
        return result;
    }

    private async Task RunAsync(Guid id, Guid certificateId, Func<CancellationToken, Task<string>> action, CancellationTokenSource linked)
    {
        try
        {
            // Persistence failures are ownership failures, not business failures to retry with another terminal write.
            if (!await MarkRunningAsync(id, CancellationToken.None)) return;
            _logger.LogInformation("Certificate operation started. OperationId={OperationId} CertificateId={CertificateId}", id, certificateId);
            var state = CertificateOperationState.Failed;
            string problem;
            try
            {
                var certificateGate = _certificateGates.GetOrAdd(certificateId, static _ => new SemaphoreSlim(1, 1));
                await certificateGate.WaitAsync(linked.Token);
                try { linked.Token.ThrowIfCancellationRequested(); problem = await action(linked.Token); }
                finally { certificateGate.Release(); }
                state = linked.IsCancellationRequested ? CertificateOperationState.Cancelled : string.IsNullOrEmpty(problem) ? CertificateOperationState.Succeeded : CertificateOperationState.Failed;
                if (linked.IsCancellationRequested) problem = "certificate.operation_cancelled";
            }
            catch (CertificateChallengeCleanupException) { throw; }
            catch (ResidentWorkCompletionException) { throw; }
            catch (CertificateOperationException error) { problem = error.ProblemCode; }
            catch (OperationCanceledException) { state = CertificateOperationState.Cancelled; problem = "certificate.operation_cancelled"; }
            catch (Exception error) { _logger.LogError(error, "Certificate operation failed. OperationId={OperationId}", id); problem = "certificate.operation_failed"; }
            await CompleteAsync(id, state, problem, CancellationToken.None);
        }
        finally { _cancellations.TryRemove(id, out _); linked.Dispose(); }
    }

    private async Task<bool> MarkRunningAsync(Guid id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var operation = _operations[id];
            if (operation.State == CertificateOperationState.Cancelled) return false;
            _operations[id] = operation with { State = CertificateOperationState.Running, Stage = "running", StartedAt = DateTimeOffset.UtcNow };
            try { await SaveAsync(ct); }
            catch { _operations[id] = operation; throw; }
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
            var interrupted = problemCode == InterruptedProblem;
            var completed = operation with { State = state, Stage = interrupted ? "interrupted" : state.ToString().ToLowerInvariant(),
                ProblemCode = problemCode, CompletedAt = DateTimeOffset.UtcNow,
                Order = interrupted || state == CertificateOperationState.Cancelled ? operation.Order : null, Challenges = null };
            _operations[id] = completed;
            try
            {
                await SaveAsync(ct);
                _logger.LogInformation("Certificate operation completed. OperationId={OperationId} CertificateId={CertificateId} Kind={Kind} State={State} ProblemCode={ProblemCode}",
                    id, completed.CertificateId, completed.Kind, state, problemCode);
                if (completed.Kind == "renew" && !interrupted)
                {
                    if (state == CertificateOperationState.Failed)
                        await _certificates.UpdateRenewalOutcomeAsync(completed.CertificateId, null, problemCode, ct);
                    await _renewalAttempts.RecordAsync(completed.ToDto(), ct);
                }
            }
            catch { _operations[id] = operation; throw; }
        }
        finally { _gate.Release(); }
    }

    private async Task LoadAndRecoverAsync(CancellationToken ct)
    {
        if (Directory.Exists(_path)) throw new IOException("Certificate operation ledger path is not a file.");
        if (!File.Exists(_path)) return;
        try
        {
            foreach (var item in JsonSerializer.Deserialize<PersistedOperation[]>(await File.ReadAllTextAsync(_path, ct), Json) ?? throw new JsonException("Certificate ledger must be an array."))
            {
                foreach (var challenge in item.Challenges ?? [])
                    await (_challengeFiles ?? throw new InvalidOperationException("WebRoot cleanup ownership is unavailable.")).RemoveOwnedAsync(challenge, ct);
                var recovered = item.State is CertificateOperationState.Queued or CertificateOperationState.Running
                    ? item with { State = CertificateOperationState.Failed, Stage = "interrupted", ProblemCode = InterruptedProblem, CompletedAt = DateTimeOffset.UtcNow, Challenges = null }
                    : item;
                _operations[recovered.OperationId] = recovered;
                _byIdempotency[recovered.IdempotencyKey] = recovered.OperationId;
            }
            await SaveAsync(ct);
        }
        catch (JsonException error) { throw new InvalidOperationException("Certificate operation ledger is invalid; its responsibility cannot be established.", error); }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        await SaveLedgerAsync(ct);
        await _journal.UpsertAsync(_operations.Values.Select(operation => new CertificateOperationJournalEntry(operation.ToDto(), operation.IdempotencyKey, operation.Actor)), ct);
    }

    private async Task SaveLedgerAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(output, _operations.Values, Json, ct);
            output.Flush(flushToDisk: true);
        }
        File.Move(temp, _path, true);
    }

    private sealed record PersistedOperation(Guid OperationId, string IdempotencyKey, Guid CertificateId, string Kind, string? Actor,
        CertificateOperationState State, string Stage, string ProblemCode, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AcmeOrderIdentity? Order = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WebRootChallengeFile[]? Challenges = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? WebsitePublicationId = null)
    {
        public CertificateOperationDto ToDto() => new(OperationId, CertificateId, Kind, State, Stage, ProblemCode, StartedAt, CompletedAt);
    }
}

internal sealed class CertificateChallengeCleanupException(IReadOnlyList<Exception> failures)
    : InvalidOperationException("Certificate challenge cleanup is unconfirmed.", new AggregateException(failures));
