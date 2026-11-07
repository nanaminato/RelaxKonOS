using Microsoft.Data.Sqlite;
using System.Text.Json;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;

namespace RelaxKonOS.Server.Proxy;

public delegate Task ProxyOperationStageReporter(string stage);

/// <summary>
/// Durable, host-global operation ledger for typed Proxy mutations.  It queues delegates supplied
/// by domain services only; it is deliberately not a generic command executor.
/// </summary>
public sealed partial class ProxyOperationStore(IProxyPlatformPaths paths, ILogger<ProxyOperationStore> logger,
    IProxyWorkLeaseSource workLeases, MihomoPublicationStore? publication = null,
    IProxySubscriptionRepository? subscriptions = null) : IHostedService
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
            return entries.Where(IsUnresolved)
                .Select(entry => $"Proxy operation {entry.Operation.OperationId} has unresolved execution or interruption responsibility.").ToArray();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or RelaxKonOS.ManagedRuntimes.RuntimePolicyException)
        { return ["Proxy operation ledger cannot be read or verified; task responsibility is unknown."]; }
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    // The Host owns the service graph. A caller may stop waiting, but Host shutdown must
    // keep the graph alive until admitted work has finished cleanup and terminal persistence.
    Task IHostedService.StopAsync(CancellationToken ct) => StopAsync(CancellationToken.None);

    public async Task StopAsync(CancellationToken ct)
    {
        Volatile.Write(ref _stopping, true);
        Task[] pending;
        await _gate.WaitAsync(ct);
        try { pending = _running.Values.Select(item => item.Task).ToArray(); }
        finally { _gate.Release(); }
        await Task.WhenAll(pending).WaitAsync(ct);
    }

    public Task<ProxyOperationDto> EnqueueAsync(string idempotencyKey, string kind, Func<ProxyOperationStageReporter, CancellationToken, Task<string?>> operation, CancellationToken cancellationToken) =>
        EnqueueIdentifiedAsync(idempotencyKey, kind, (_, stage, token) => operation(stage, token), cancellationToken);

    internal async Task<ProxyOperationDto> EnqueueIdentifiedAsync(string idempotencyKey, string kind,
        Func<Guid, ProxyOperationStageReporter, CancellationToken, Task<string?>> operation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
            throw new ProxyOperationValidationException(ProxyProblemCodes.IdempotencyKeyRequired);
        await _gate.WaitAsync(cancellationToken);
        ProxyOperationDto item;
        RelaxKonOS.Server.Residency.IModuleWorkLease? work = null;
        try
        {
            var entries = await ReadEntriesAsync(paths, cancellationToken);
            if (entries.Any(NeedsReconciliation))
                await ReconcileRefreshesAsync(entries, null, cancellationToken);
            var prior = entries.FirstOrDefault(entry => entry.IdempotencyKey == idempotencyKey);
            if (prior is not null) return Visible(prior.Operation);
            if (Volatile.Read(ref _stopping) || _recoveryBlocked || entries.Any(entry => IsUnresolved(entry) && !_running.ContainsKey(entry.Operation.OperationId)))
                throw new ProxyOperationValidationException(ProxyProblemCodes.OperationInterrupted);
            work = await workLeases.AcquireAsync(cancellationToken);
            item = new ProxyOperationDto(Guid.NewGuid(), kind, ProxyOperationState.Queued, "queued", "", null, null);
            await SaveAsync([.. entries, new Entry(idempotencyKey, item, kind == RefreshBatchKind ? [] : null)], cancellationToken);
            _running.Add(item.OperationId, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        catch { work?.Dispose(); throw; }
        finally { _gate.Release(); }

        logger.LogInformation("Proxy operation {OperationId} queued: {OperationKind}.", item.OperationId, kind);
        _ = ExecuteTrackedAsync(item, (stage, token) => operation(item.OperationId, stage, token), work!);
        return item;
    }

    internal async Task<ProxyOperationDto?> ResumeInboundPublicationAsync(Guid? pending, IReadOnlySet<Guid> known,
        Func<Guid, CancellationToken, Task<string?>> recover, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        RelaxKonOS.Server.Residency.IModuleWorkLease? work = null;
        ProxyOperationDto original;
        try
        {
            var entries = await ReadEntriesAsync(paths, ct);
            var item = entries.FirstOrDefault(entry =>
                (entry.Operation.OperationId == pending || known.Contains(entry.Operation.OperationId))
                && (IsUnresolved(entry) || entry.Operation.OperationId == pending && entry.Operation.State == ProxyOperationState.Failed))?.Operation;
            if (item is null) return null;
            if (item.Kind is not ("proxy_server.configuration.apply" or "proxy_server.configuration.rollback"))
                throw new ProxyOperationValidationException(ProxyProblemCodes.OperationInterrupted);
            original = item;
            var id = original.OperationId;
            if (_running.ContainsKey(id)) return original;
            if (Volatile.Read(ref _stopping) || _recoveryBlocked
                || entries.Any(entry => entry.Operation.OperationId != id && IsUnresolved(entry)
                    && !_running.ContainsKey(entry.Operation.OperationId) && entry.Operation.OperationId != pending
                    && !known.Contains(entry.Operation.OperationId)))
                throw new ProxyOperationValidationException(ProxyProblemCodes.OperationInterrupted);
            work = await workLeases.AcquireAsync(ct);
            _running.Add(id, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        catch { work?.Dispose(); throw; }
        finally { _gate.Release(); }
        _ = ExecuteTrackedAsync(original, (_, token) => recover(original.OperationId, token), work!);
        return original;
    }

    private async Task ExecuteTrackedAsync(ProxyOperationDto original, Func<ProxyOperationStageReporter, CancellationToken, Task<string?>> operation, RelaxKonOS.Server.Residency.IModuleWorkLease work)
    {
        var id = original.OperationId;
        Exception? failure = null;
        try { await ExecuteAsync(original, operation); }
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
            var index = Array.FindIndex(entries, entry => entry.Operation.OperationId == id);
            if (index < 0) return null;
            if (NeedsReconciliation(entries[index])) await ReconcileRefreshesAsync(entries, id, cancellationToken);
            return Visible(entries[index].Operation);
        }
        finally { entries = null; _gate.Release(); }
    }

    private bool IsInterruptedRefresh(ProxyOperationDto operation) =>
        operation.Kind == "subscription.refresh" && !_running.ContainsKey(operation.OperationId)
        && (operation.State == ProxyOperationState.Running
            || operation.State == ProxyOperationState.Interrupted && operation.Stage == "publication_pending"
                && operation.ProblemCode == ProxyProblemCodes.OperationInterrupted);

    private bool NeedsReconciliation(Entry entry) => IsInterruptedRefresh(entry.Operation)
        || entry.Batch is not null && !_running.ContainsKey(entry.Operation.OperationId)
            && (entry.Operation.State is ProxyOperationState.Queued or ProxyOperationState.Running || entry.Operation.Stage == BatchPending);

    // The queue gate comes first. Draft writers release the publication gate before recording queue progress.
    private async Task ReconcileRefreshesAsync(Entry[] entries, Guid? requested, CancellationToken ct)
    {
        if (Volatile.Read(ref _stopping)) return;
        MihomoPublicationState? state;
        try { state = publication is null ? null : await publication.ReadAsync(ct); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or RelaxKonOS.ManagedRuntimes.RuntimePolicyException)
        { return; }
        using var work = await workLeases.AcquireAsync(ct);
        var changed = false;
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var operation = entry.Operation;
            if (!NeedsReconciliation(entry) || requested is not null && requested != operation.OperationId)
                continue;

            if (entry.Batch is not null)
            {
                var reconciled = await ReconcileBatchAsync(entry, state, ct);
                if (reconciled == entry) continue;
                entries[index] = reconciled;
                changed = true;
                continue;
            }

            if (state?.SubscriptionRefreshCompletions?.GetValueOrDefault(operation.OperationId) is { } proof)
                operation = operation with
                {
                    State = proof.Confirmed ? ProxyOperationState.Succeeded : ProxyOperationState.Failed,
                    Stage = proof.Confirmed ? "completed" : "failed",
                    ProblemCode = proof.Confirmed ? "" : ProxyProblemCodes.ConfigApplyFailed,
                    CompletedAt = proof.CompletedAt
                };
            else
            {
                var draft = state is not null && HasPendingRefresh(state, operation.OperationId)
                    ? (Pending: true, Receipt: (ProxyDraftRefreshReceipt?)null)
                    : await ReconcileDraftAsync(operation.OperationId, null, ct);
                if (draft.Receipt is { } receipt)
                    operation = operation with
                    {
                        State = receipt.Applied ? ProxyOperationState.Succeeded : ProxyOperationState.Failed,
                        Stage = receipt.Applied ? "completed" : "failed",
                        ProblemCode = receipt.Applied ? "" : ProxyProblemCodes.ConfigApplyFailed,
                        CompletedAt = receipt.Applied ? DateTimeOffset.UtcNow : receipt.At
                    };
                else if (draft.Pending)
                    operation = operation with
                    {
                        State = ProxyOperationState.Interrupted,
                        Stage = "publication_pending",
                        ProblemCode = ProxyProblemCodes.OperationInterrupted,
                        CompletedAt = null
                    };
            }

            if (operation == entry.Operation) continue;
            entries[index] = entry with { Operation = operation };
            changed = true;
        }
        if (!changed) return;
        await SaveAsync(entries, CancellationToken.None);
    }

    private Task<(bool Pending, ProxyDraftRefreshReceipt? Receipt)> ReconcileDraftAsync(Guid id, Guid? subscription, CancellationToken ct) =>
        subscriptions is not null && publication is not null
            ? new ProxyDraftRefreshRecovery(paths, subscriptions, publication).ReconcileAsync(id, subscription, ct)
            : Task.FromResult<(bool, ProxyDraftRefreshReceipt?)>(default);

    private static bool HasPendingRefresh(MihomoPublicationState state, Guid id) =>
        (state.Pending?.OperationId == id && state.Pending.Profile?.Refresh is not null)
        || (state.DirectReload?.OperationId == id && state.DirectReload.Profile?.Refresh is not null);

    private ProxyOperationDto Visible(ProxyOperationDto operation) =>
        operation.State is ProxyOperationState.Queued or ProxyOperationState.Running && !_running.ContainsKey(operation.OperationId)
            ? operation with { State = ProxyOperationState.Interrupted, Stage = "interrupted", ProblemCode = ProxyProblemCodes.OperationInterrupted, CompletedAt = DateTimeOffset.UtcNow }
            : operation;

    private async Task ExecuteAsync(ProxyOperationDto original, Func<ProxyOperationStageReporter, CancellationToken, Task<string?>> operation)
    {
        var id = original.OperationId;
        try
        {
            await UpdateAsync(id, item => item with { State = ProxyOperationState.Running, Stage = "running",
                StartedAt = item.StartedAt ?? DateTimeOffset.UtcNow, CompletedAt = null });
            var problem = await operation(async stage =>
            {
                logger.LogDebug("Proxy operation {OperationId} entered stage {Stage}.", id, stage);
                await UpdateAsync(id, item => item with { Stage = stage });
            }, CancellationToken.None);
            var publicationPending = false;
            var batchPending = original.Kind == RefreshBatchKind && problem == ProxyProblemCodes.RecoveryRequired;
            if (original.Kind == "subscription.refresh" && problem == ProxyProblemCodes.RecoveryRequired && publication is not null)
            {
                try
                {
                    var state = await publication.ReadAsync(CancellationToken.None);
                    publicationPending = HasPendingRefresh(state, id)
                        || state.SubscriptionRefreshCompletions?.ContainsKey(id) == true
                        || subscriptions is not null && await subscriptions.ReadDraftRefreshIntentAsync(id, CancellationToken.None) is not null;
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or RelaxKonOS.ManagedRuntimes.RuntimePolicyException)
                { publicationPending = true; }
                if (publicationPending) problem = ProxyProblemCodes.OperationInterrupted;
            }
            if (!string.IsNullOrEmpty(problem))
                logger.LogWarning("Proxy operation {OperationId} failed with {ProblemCode}.", id, problem);
            else
                logger.LogInformation("Proxy operation {OperationId} completed.", id);
            await UpdateAsync(id, item => item with
            {
                State = publicationPending || batchPending ? ProxyOperationState.Interrupted
                    : string.IsNullOrEmpty(problem) ? ProxyOperationState.Succeeded : ProxyOperationState.Failed,
                Stage = batchPending ? BatchPending : publicationPending ? "publication_pending" : string.IsNullOrEmpty(problem) ? "completed" : "failed",
                ProblemCode = batchPending ? ProxyProblemCodes.OperationInterrupted : problem ?? "",
                CompletedAt = publicationPending || batchPending ? null : DateTimeOffset.UtcNow,
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
            await UpdateEntryAsync(id, entry =>
            {
                var pendingStep = exception is not ProxyOperationCleanupException
                    && entry.Batch?.Any(step => step.State == RefreshStepState.Running) == true;
                return entry with { Operation = entry.Operation with
                {
                    State = pendingStep ? ProxyOperationState.Interrupted : ProxyOperationState.Failed,
                    Stage = pendingStep ? BatchPending : "failed",
                    ProblemCode = ProxyProblemCodes.OperationInterrupted,
                    CompletedAt = pendingStep ? null : DateTimeOffset.UtcNow
                } };
            });
            if (exception is ProxyOperationCleanupException) throw;
        }
    }

    private async Task UpdateAsync(Guid id, Func<ProxyOperationDto, ProxyOperationDto> update)
        => await UpdateEntryAsync(id, entry => entry with { Operation = update(entry.Operation) });

    private async Task UpdateEntryAsync(Guid id, Func<Entry, Entry> update)
    {
        await _gate.WaitAsync();
        try
        {
            var entries = await ReadEntriesAsync(paths, CancellationToken.None);
            var index = Array.FindIndex(entries, entry => entry.Operation.OperationId == id);
            if (index < 0) throw new InvalidDataException("Proxy operation responsibility disappeared before terminal confirmation.");
            entries[index] = update(entries[index]);
            await SaveAsync(entries, CancellationToken.None);
        }
        finally { _gate.Release(); }
    }

    private static bool IsUnresolved(Entry entry) => !IsSettledBatchInterruption(entry) && IsUnresolved(entry.Operation);

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
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.IdempotencyKey) || entry.IdempotencyKey.Length > 200
                || !keys.Add(entry.IdempotencyKey) || entry.Operation is not { } item || item.OperationId == Guid.Empty
                || !ids.Add(item.OperationId) || !Enum.IsDefined(item.State) || string.IsNullOrEmpty(item.Kind)
                || item.Stage is null || item.ProblemCode is null || !IsValidBatch(entry))
                throw new InvalidDataException("Invalid Proxy operation ledger entry.");
            if (entry.Batch is { } steps && steps.Any(step => !ids.Add(step.OperationId)))
                throw new InvalidDataException("Duplicate Proxy refresh operation ID.");
        }
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

    private sealed record Entry(string IdempotencyKey, ProxyOperationDto Operation,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        RefreshStep[]? Batch = null);

    private static void SetPrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

public sealed class ProxyOperationValidationException(string problemCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
}
