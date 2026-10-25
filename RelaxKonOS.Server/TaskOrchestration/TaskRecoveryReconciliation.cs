using System.Collections.Immutable;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;

namespace RelaxKonOS.Server.TaskOrchestration;

public sealed partial class TaskPlanStore
{
    /// <summary>Append new host evidence for an explicitly requested recovery check. Never renews execution authority.</summary>
    public TaskRun RecordRecoveryReconciliation(Guid runId, string subject, TaskReconciliationEvidence evidence, long expectedRevision) => Guard(() =>
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var stored = ReadRun(connection, transaction, runId, subject) ?? throw new TaskPlanStoreException("task.run_not_found", 404);
        var run = stored.Record;
        var step = run.Steps.SingleOrDefault(item => item.StepId == evidence.StepId) ?? throw new TaskPlanStoreException("task.step_not_found", 404);
        var dispatch = stored.Dispatches.SingleOrDefault(item => item.StepId == evidence.StepId) ?? throw new TaskPlanStoreException("task.step_not_ready", 409);
        if (step.DomainOperationId != evidence.Operation.OperationId || evidence.Operation.Service != dispatch.Service || evidence.Operation.Kind != dispatch.Kind)
            throw new TaskPlanStoreException("task.domain_operation_conflict", 409);
        if (stored.RecoveryResults.LastOrDefault(item => item.StepId == step.StepId) is { } last && Fingerprint(last) == Fingerprint(evidence)) return run;
        if (run.Revision != expectedRevision) throw new TaskPlanStoreException("task.run_revision_conflict", 409);
        if (run.State != TaskRunState.NeedsRecovery || step.State != TaskStepRunState.NeedsRecovery)
            throw new TaskPlanStoreException("task.recovery_not_required", 409);
        if (InstallationOperationStoreActive(evidence.Operation)) throw new TaskPlanStoreException("task.domain_operation_active", 409);
        if (stored.RecoveryResults.Length >= 32) throw new TaskPlanStoreException("task.recovery_check_capacity_exceeded", 409);
        var proposal = Read(connection, transaction, run.PlanId, subject)!.Record.Plan.Semantics.Steps.Single(item => item.StepId == step.StepId);
        var outcome = ClassifyRecoveryResult(proposal, evidence, run.Steps.Any(item => item.State == TaskStepRunState.Skipped));
        var steps = run.Steps.Select(item => item.StepId == step.StepId ? item with { State = outcome, ProblemCode = ResultProblem(outcome) } : item).ToImmutableArray();
        var problem = steps.FirstOrDefault(item => item.State is TaskStepRunState.Failed or TaskStepRunState.Cancelled or TaskStepRunState.NeedsRecovery)?.ProblemCode;
        var changed = stored with
        {
            Record = run with { Revision = run.Revision + 1, Steps = steps, State = ResultRunState(steps, run.CancellationRequested), ProblemCode = problem },
            RecoveryResults = [.. stored.RecoveryResults, evidence],
            Audit = [.. stored.Audit, new("recovery_checked", step.StepId, _clock.GetUtcNow())]
        };
        UpdateRun(connection, transaction, changed);
        _beforeCommit?.Invoke(TaskPlanCommitPoint.RecoveryResultWritten);
        transaction.Commit();
        return changed.Record;
    });

    private static TaskStepRunState ClassifyRecoveryResult(TaskStep proposal, TaskReconciliationEvidence evidence, bool hasSkippedSteps)
    {
        // After Server restart the domain ledger may be Interrupted even though the Helper has a
        // conclusive receipt. Only this explicit path may combine that receipt with fresh host facts.
        var observed = evidence;
        if (evidence.Operation.State == InstallationOperationState.Interrupted && evidence.Snapshot?.Receipt is { } receipt)
        {
            var state = receipt.Outcome switch
            {
                RuntimeSwitchOutcome.Succeeded => InstallationOperationState.Succeeded,
                RuntimeSwitchOutcome.Failed => InstallationOperationState.Failed,
                RuntimeSwitchOutcome.Cancelled => InstallationOperationState.Cancelled,
                _ => InstallationOperationState.Interrupted
            };
            observed = evidence with { Operation = evidence.Operation with { State = state } };
        }
        var outcome = ClassifyResult(proposal, observed);
        if (outcome == TaskStepRunState.Succeeded && evidence.Snapshot?.ConfigurationIdentity
            != TaskPlanJson.ReadInputs<RuntimeTaskFrozenChange>(proposal.Inputs).ConfigurationIdentity)
            return TaskStepRunState.NeedsRecovery;
        // Reconciliation cannot resume previously skipped steps or call a partial plan successful.
        return outcome == TaskStepRunState.Succeeded && hasSkippedSteps ? TaskStepRunState.NeedsRecovery : outcome;
    }
}

/// <summary>Only queries the original domain operation and host receipt; no dispatch, recovery or authentication of an administrator.</summary>
public sealed class TaskRecoveryReconciliationService(TaskPlanStore store, ITaskReconciliationDomain domain)
{
    public async Task<TaskRun> RecheckAsync(Guid runId, string subject, long expectedRevision, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var run = store.GetRun(runId, subject) ?? throw new TaskPlanStoreException("task.run_not_found", 404);
        if (run.Revision != expectedRevision) throw new TaskPlanStoreException("task.run_revision_conflict", 409);
        if (run.State != TaskRunState.NeedsRecovery) throw new TaskPlanStoreException("task.recovery_not_required", 409);
        var step = run.Steps.Single(item => item.State == TaskStepRunState.NeedsRecovery);
        var dispatch = store.GetDispatch(runId, step.StepId, subject) ?? throw new TaskPlanStoreException("task.step_not_ready", 409);
        var operation = domain.Find(runId, dispatch, subject) ?? throw new TaskPlanStoreException("task.domain_operation_conflict", 409);
        if (operation.OperationId != step.DomainOperationId || operation.Service != dispatch.Service || operation.Kind != dispatch.Kind)
            throw new TaskPlanStoreException("task.domain_operation_conflict", 409);
        if (operation.State is InstallationOperationState.Queued or InstallationOperationState.Running)
            throw new TaskPlanStoreException("task.domain_operation_active", 409);
        var snapshot = await domain.ObserveAsync(operation.OperationId, ct);
        ct.ThrowIfCancellationRequested();
        return store.RecordRecoveryReconciliation(runId, subject, new(step.StepId, operation, snapshot), expectedRevision);
    }
}
