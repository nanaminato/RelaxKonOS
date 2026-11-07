using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;

namespace RelaxKonOS.Server.Proxy;

public sealed partial class ProxyOperationStore
{
    internal const string RefreshBatchKind = "subscription.refresh_all";
    private const string BatchPlanned = "batch_planned";
    private const string BatchPending = "batch_pending";
    private const string BatchInterrupted = "batch_interrupted";

    internal enum RefreshStepState { Planned, Running, Succeeded, Failed, Skipped }

    internal sealed record RefreshStep(Guid SubscriptionId, Guid OperationId, RefreshStepState State);

    internal async Task<IReadOnlyList<RefreshStep>> PlanRefreshBatchAsync(Guid id, IEnumerable<Guid> subscriptions, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var plan = subscriptions.Select(subscription => new RefreshStep(subscription, Guid.NewGuid(), RefreshStepState.Planned)).ToArray();
        await UpdateEntryAsync(id, entry =>
        {
            if (entry.Operation is not { Kind: RefreshBatchKind, State: ProxyOperationState.Running, Stage: "running" }
                || entry.Batch is not { Length: 0 })
                throw new InvalidDataException("The original refresh batch cannot be planned again.");
            var planned = entry with { Batch = plan, Operation = entry.Operation with { Stage = BatchPlanned } };
            if (!IsValidBatch(planned)) throw new InvalidDataException("Invalid subscription refresh batch plan.");
            return planned;
        });
        return plan;
    }

    internal Task StartRefreshStepAsync(Guid batch, Guid step) => UpdateEntryAsync(batch, entry =>
    {
        var steps = RequireLiveBatch(entry);
        var next = Array.FindIndex(steps, item => item.State != RefreshStepState.Succeeded);
        if (next < 0 || steps[next].OperationId != step || steps[next].State != RefreshStepState.Planned)
            throw new InvalidDataException("The refresh step is not next in the original batch.");
        steps[next] = steps[next] with { State = RefreshStepState.Running };
        return entry;
    });

    internal Task FinishRefreshStepAsync(Guid batch, Guid step, string? problem) => UpdateEntryAsync(batch, entry =>
    {
        var steps = RequireLiveBatch(entry);
        var current = Array.FindIndex(steps, item => item.OperationId == step && item.State == RefreshStepState.Running);
        if (current < 0) throw new InvalidDataException("The running refresh step was lost.");
        if (string.IsNullOrEmpty(problem))
            steps[current] = steps[current] with { State = RefreshStepState.Succeeded };
        else
        {
            steps[current] = steps[current] with { State = RefreshStepState.Failed };
            for (var index = current + 1; index < steps.Length; index++)
                steps[index] = steps[index] with { State = RefreshStepState.Skipped };
        }
        return entry;
    });

    private static RefreshStep[] RequireLiveBatch(Entry entry) =>
        entry.Operation is { State: ProxyOperationState.Running, Stage: BatchPlanned } && entry.Batch is { } steps
            ? steps : throw new InvalidDataException("Missing running subscription batch.");

    private async Task<Entry> ReconcileBatchAsync(Entry entry, MihomoPublicationState? publication, CancellationToken ct)
    {
        var steps = entry.Batch!;
        var recovered = new RefreshStep[steps.Length];
        for (var index = 0; index < steps.Length; index++)
        {
            var step = steps[index];
            recovered[index] = step.State switch
            {
                RefreshStepState.Planned => step with { State = RefreshStepState.Skipped },
                RefreshStepState.Running => step with { State = await ReconcileRunningStepAsync(step, publication, ct) },
                _ => step
            };
        }
        var pending = recovered.Any(step => step.State == RefreshStepState.Running);
        var stage = pending ? BatchPending : BatchInterrupted;
        if (entry.Operation.Stage == stage && steps.SequenceEqual(recovered)) return entry;
        return entry with
        {
            Batch = recovered,
            Operation = entry.Operation with
            {
                State = ProxyOperationState.Interrupted,
                Stage = stage,
                ProblemCode = ProxyProblemCodes.OperationInterrupted,
                CompletedAt = pending ? null : DateTimeOffset.UtcNow
            }
        };
    }

    private async Task<RefreshStepState> ReconcileRunningStepAsync(RefreshStep step, MihomoPublicationState? publication, CancellationToken ct)
    {
        if (publication?.SubscriptionRefreshCompletions?.GetValueOrDefault(step.OperationId) is { } proof)
            return proof.Confirmed ? RefreshStepState.Succeeded : RefreshStepState.Failed;
        if (publication is not null && HasPendingRefresh(publication, step.OperationId)) return RefreshStepState.Running;
        return (await ReconcileDraftAsync(step.OperationId, step.SubscriptionId, ct)).Receipt?.Applied switch
        {
            true => RefreshStepState.Succeeded,
            false => RefreshStepState.Failed,
            null => RefreshStepState.Running
        };
    }

    private static bool IsSettledBatchInterruption(Entry entry) =>
        entry.Batch is not null && entry.Operation is { State: ProxyOperationState.Interrupted, Stage: BatchInterrupted };

    private static bool IsValidBatch(Entry entry)
    {
        if (entry.Batch is not { } steps) return entry.Operation.Stage is not (BatchPlanned or BatchPending or BatchInterrupted);
        if (entry.Operation.Kind != RefreshBatchKind) return false;
        var subscriptions = new HashSet<Guid>();
        var ids = new HashSet<Guid> { entry.Operation.OperationId };
        var suffix = false;
        foreach (var step in steps)
        {
            if (step is null || step.SubscriptionId == Guid.Empty || !subscriptions.Add(step.SubscriptionId)
                || step.OperationId == Guid.Empty || !ids.Add(step.OperationId) || !Enum.IsDefined(step.State))
                return false;
            if (suffix && step.State is not (RefreshStepState.Planned or RefreshStepState.Skipped)) return false;
            suffix |= step.State != RefreshStepState.Succeeded;
        }
        return entry.Operation.Stage switch
        {
            "queued" or "running" => steps.Length == 0,
            BatchPlanned => entry.Operation.State == ProxyOperationState.Running,
            BatchInterrupted => entry.Operation is { State: ProxyOperationState.Interrupted, CompletedAt: not null, ProblemCode: ProxyProblemCodes.OperationInterrupted }
                && steps.All(step => step.State is not (RefreshStepState.Planned or RefreshStepState.Running)),
            BatchPending => entry.Operation is { State: ProxyOperationState.Interrupted, CompletedAt: null, ProblemCode: ProxyProblemCodes.OperationInterrupted }
                && steps.Any(step => step.State == RefreshStepState.Running),
            _ => true
        };
    }
}
