using System.Collections.Immutable;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;

namespace RelaxKonOS.Server.TaskOrchestration;

/// <summary>Historical evidence, not a new authorization. A missing host observation cannot prove success.</summary>
public sealed record TaskReconciliationEvidence(string StepId, InstallationOperationDto Operation,
    RuntimeInstallationSnapshot? Snapshot);

public sealed partial class TaskPlanStore
{
    public TaskRun RecordReconciliation(Guid runId, string subject, TaskReconciliationEvidence evidence, long expectedRevision) => Guard(() =>
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var stored = ReadRun(connection, transaction, runId, subject) ?? throw new TaskPlanStoreException("task.run_not_found", 404);
        var run = stored.Record;
        var step = run.Steps.SingleOrDefault(item => item.StepId == evidence.StepId) ?? throw new TaskPlanStoreException("task.step_not_found", 404);
        var dispatch = stored.Dispatches.SingleOrDefault(item => item.StepId == evidence.StepId)
            ?? throw new TaskPlanStoreException("task.step_not_ready", 409);
        if (step.DomainOperationId != evidence.Operation.OperationId || evidence.Operation.Service != dispatch.Service
            || evidence.Operation.Kind != dispatch.Kind || evidence.Operation.OperationId == Guid.Empty)
            throw new TaskPlanStoreException("task.domain_operation_conflict", 409);
        if (stored.Results.SingleOrDefault(item => item.StepId == evidence.StepId) is { } previous)
            return Fingerprint(previous) == Fingerprint(evidence) ? run : throw new TaskPlanStoreException("task.result_conflict", 409);
        if (run.Revision != expectedRevision) throw new TaskPlanStoreException("task.run_revision_conflict", 409);
        if (run.State is not (TaskRunState.Running or TaskRunState.Cancelling) || step.State != TaskStepRunState.Running)
            throw new TaskPlanStoreException("task.step_not_ready", 409);
        if (InstallationOperationStoreActive(evidence.Operation)) return run;
        var proposal = Read(connection, transaction, run.PlanId, subject)!.Record.Plan.Semantics.Steps.Single(item => item.StepId == evidence.StepId);
        var outcome = ClassifyResult(proposal, evidence);
        var problem = ResultProblem(outcome);
        var steps = run.Steps.Select(item => item.StepId == step.StepId ? item with { State = outcome, ProblemCode = problem }
            : outcome != TaskStepRunState.Succeeded && item.State == TaskStepRunState.Pending
                ? item with { State = TaskStepRunState.Skipped } : item).ToImmutableArray();
        var changed = stored with
        {
            Record = run with { Revision = run.Revision + 1, Steps = steps, State = ResultRunState(steps, run.CancellationRequested), ProblemCode = problem },
            Results = [.. stored.Results, evidence],
            Audit = [.. stored.Audit, new("step_" + outcome.ToString().ToLowerInvariant(), step.StepId, _clock.GetUtcNow())]
        };
        UpdateRun(connection, transaction, changed);
        _beforeCommit?.Invoke(TaskPlanCommitPoint.ResultWritten);
        transaction.Commit();
        return changed.Record;
    });

    private static bool InstallationOperationStoreActive(InstallationOperationDto operation) =>
        operation.State is InstallationOperationState.Queued or InstallationOperationState.Running;

    private static TaskStepRunState ClassifyResult(TaskStep proposal, TaskReconciliationEvidence evidence)
    {
        var operation = evidence.Operation;
        var snapshot = evidence.Snapshot;
        var frozen = TaskPlanJson.ReadInputs<RuntimeTaskFrozenChange>(proposal.Inputs);
        if (!Enum.IsDefined(operation.State) || operation.CompletedAt is null
            || snapshot is not { Installation: { } state, Receipt: { } receipt }
            || receipt.OperationId != operation.OperationId || state.HostId != frozen.Selection.HostId
            || state.RuntimeId != frozen.RuntimeId || state.InstallationId != frozen.Selection.InstallationId
            || state.StateRevision < frozen.Selection.ExpectedStateRevision || state.RecoveryRequired || snapshot.PendingOperationId is not null
            || snapshot.ProblemCode is not null)
            return TaskStepRunState.NeedsRecovery;
        if (operation.State == InstallationOperationState.Succeeded && receipt.Outcome == RuntimeSwitchOutcome.Succeeded)
        {
            var expected = frozen.Change == RuntimeChange.Uninstall
                ? state.Ownership == RuntimeOwnership.Absent && state.ActiveReleaseId is null && snapshot.MeasuredVersion is null
                : state.Ownership == RuntimeOwnership.Managed && state.ActiveReleaseId == frozen.Release?.ReleaseId
                    // sing-box reports canonical SemVer; the signed publisher label may start with 'v'.
                    // Exact canonical text also keeps build metadata significant for artifact identity.
                    && snapshot.MeasuredVersion == frozen.Release?.SortVersion;
            return expected && state.StateRevision > frozen.Selection.ExpectedStateRevision
                ? TaskStepRunState.Succeeded : TaskStepRunState.NeedsRecovery;
        }
        // A failed/cancelled transaction is conclusive only after the previous program/configuration was restored.
        var restored = state.ActiveReleaseId == frozen.PreviousReleaseId && snapshot.MeasuredVersion == frozen.CurrentVersion
            && snapshot.ConfigurationIdentity == frozen.ConfigurationIdentity
            && state.Ownership == (frozen.PreviousReleaseId is null ? RuntimeOwnership.Absent : RuntimeOwnership.Managed);
        if (restored && operation.State == InstallationOperationState.Failed && receipt.Outcome == RuntimeSwitchOutcome.Failed)
            return TaskStepRunState.Failed;
        if (restored && operation.State == InstallationOperationState.Cancelled && receipt.Outcome == RuntimeSwitchOutcome.Cancelled)
            return TaskStepRunState.Cancelled;
        return TaskStepRunState.NeedsRecovery;
    }
    private static string? ResultProblem(TaskStepRunState state) => state switch
    {
        TaskStepRunState.Succeeded => null, TaskStepRunState.Failed => "task.domain_failed",
        TaskStepRunState.Cancelled => "task.domain_cancelled", TaskStepRunState.NeedsRecovery => "task.domain_recovery_required",
        _ => throw new InvalidDataException()
    };
    private static TaskRunState ResultRunState(ImmutableArray<TaskStepRun> steps, bool cancellationRequested = false) =>
        steps.Any(item => item.State == TaskStepRunState.NeedsRecovery) ? TaskRunState.NeedsRecovery
        : steps.Any(item => item.State == TaskStepRunState.Failed) ? TaskRunState.Failed
        : steps.Any(item => item.State == TaskStepRunState.Cancelled) ? TaskRunState.Cancelled
        : steps.All(item => item.State is TaskStepRunState.Succeeded or TaskStepRunState.NoOp) ? TaskRunState.Succeeded
        : cancellationRequested ? steps.Any(item => item.State is TaskStepRunState.Dispatching or TaskStepRunState.Running) ? TaskRunState.Cancelling : TaskRunState.Cancelled
        : steps.All(item => item.State == TaskStepRunState.Pending) ? TaskRunState.Queued : TaskRunState.Running;
}
