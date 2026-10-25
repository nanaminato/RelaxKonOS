using System.Collections.Immutable;
using System.Security.Claims;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.Installations;

namespace RelaxKonOS.Server.TaskOrchestration;

public sealed partial class TaskPlanStore
{
    internal void ValidateDispatchAcceptance(Guid runId, string stepId, TaskExecutionIdentity identity, string requestDigest) => Guard(() =>
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var stored = ReadRun(connection, transaction, runId, identity.CanonicalSubject) ?? throw new TaskPlanStoreException("task.run_not_found", 404);
        var run = stored.Record;
        var approval = ReadApproval(connection, transaction, run.ApprovalId, identity.CanonicalSubject)!.Record;
        RequireSameIdentity(approval, identity);
        if (approval.State != TaskApprovalState.Consumed || approval.RunAuthorizationExpiresAt <= _clock.GetUtcNow())
            throw new TaskPlanStoreException("task.approval_unavailable", 403);
        var step = run.Steps.SingleOrDefault(item => item.StepId == stepId);
        if (run.CancellationRequested || run.State != TaskRunState.Running || step is not { State: TaskStepRunState.Dispatching, DomainOperationId: null }
            || step.RequestDigest != requestDigest || Read(connection, transaction, run.PlanId, identity.CanonicalSubject)!.Record.Validity != TaskPlanValidity.Resolved)
            throw new TaskPlanStoreException("task.step_not_ready", 409);
        return true;
    });

    internal TaskRun CloseUnacceptedDispatchCancellation(Guid runId, string subject, string stepId, string requestDigest, long expectedRevision) => Guard(() =>
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var stored = ReadRun(connection, transaction, runId, subject) ?? throw new TaskPlanStoreException("task.run_not_found", 404);
        var run = stored.Record;
        var step = run.Steps.SingleOrDefault(item => item.StepId == stepId);
        if (run.State != TaskRunState.Cancelling || !run.CancellationRequested || run.Revision != expectedRevision
            || step is not { State: TaskStepRunState.Dispatching, DomainOperationId: null } || step.RequestDigest != requestDigest)
            throw new TaskPlanStoreException("task.step_not_ready", 409);
        const string problem = "task.cancelled_before_acceptance";
        var steps = run.Steps.Select(item => item.StepId == stepId ? item with { State = TaskStepRunState.Cancelled, ProblemCode = problem } : item).ToImmutableArray();
        var changed = stored with
        {
            Record = run with { Revision = run.Revision + 1, Steps = steps, State = ResultRunState(steps, true), ProblemCode = problem },
            Audit = [.. stored.Audit, new("dispatch_cancelled", stepId, _clock.GetUtcNow())]
        };
        UpdateRun(connection, transaction, changed);
        _beforeCommit?.Invoke(TaskPlanCommitPoint.UnacceptedCancellationWritten);
        transaction.Commit(); return changed.Record;
    });

    public TaskRun RequestRunCancellation(Guid runId, string subject, long expectedRevision) => Guard(() =>
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var stored = ReadRun(connection, transaction, runId, subject) ?? throw new TaskPlanStoreException("task.run_not_found", 404);
        var run = stored.Record;
        if (run.CancellationRequested || run.State is not (TaskRunState.Queued or TaskRunState.Running)) return run;
        if (run.Revision != expectedRevision) throw new TaskPlanStoreException("task.run_revision_conflict", 409);
        var steps = run.Steps.Select(step => step.State == TaskStepRunState.Pending ? step with { State = TaskStepRunState.Skipped } : step).ToImmutableArray();
        var changed = stored with
        {
            Record = run with { Revision = run.Revision + 1, CancellationRequested = true, Steps = steps, State = ResultRunState(steps, true) },
            Audit = [.. stored.Audit, new("cancel_requested", null, _clock.GetUtcNow())]
        };
        UpdateRun(connection, transaction, changed);
        _beforeCommit?.Invoke(TaskPlanCommitPoint.CancellationWritten);
        transaction.Commit(); return changed.Record;
    });
}

/// <summary>Durable request before domain delivery. Does not claim cancellation until the original operation is reconciled.</summary>
public sealed class TaskRunCancellationService(TaskPlanStore store, ITaskExecutionPolicyReader policies,
    ITaskReconciliationDomain domain, InstallationCoordinator installations)
{
    public async Task<TaskRun> RequestAsync(ClaimsPrincipal principal, Guid runId, long expectedRevision, CancellationToken ct)
    {
        var subject = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (principal.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(subject)) throw new TaskApprovalException("task.session_unavailable");
        var run = store.GetRun(runId, subject) ?? throw new TaskPlanStoreException("task.run_not_found", 404);
        var live = policies.Read(principal, run.HostId);
        if (live.Identity.CanonicalSubject != subject || live.Identity.HostId != run.HostId)
            throw new TaskApprovalException("task.permission_denied");
        run = store.RequestRunCancellation(runId, subject, expectedRevision);
        if (run.State != TaskRunState.Cancelling) return run;
        foreach (var step in run.Steps.Where(item => item.State is TaskStepRunState.Running or TaskStepRunState.Dispatching))
        {
            var dispatch = store.GetDispatch(runId, step.StepId, subject)!;
            var operation = domain.Find(runId, dispatch, subject);
            if (operation is null && step is { State: TaskStepRunState.Dispatching, DomainOperationId: null })
            {
                var request = TaskPlanJson.ReadInputs<ManagedRuntimeInstallationRequest>(dispatch.Request);
                ct.ThrowIfCancellationRequested();
                if (installations.TryCloseUnacceptedTask(new(runId, step.StepId, dispatch.RequestDigest), dispatch.Kind, dispatch.Request,
                    subject, request.Plan.Request.IdempotencyKey, () => store.CloseUnacceptedDispatchCancellation(runId, subject,
                        step.StepId, dispatch.RequestDigest, run.Revision)))
                { run = store.GetRun(runId, subject)!; continue; }
                operation = domain.Find(runId, dispatch, subject);
            }
            if (operation is null) continue;
            run = store.BindDomainOperation(runId, step.StepId, subject, dispatch.RequestDigest, operation.OperationId, run.Revision);
            if (operation.State is not (InstallationOperationState.Queued or InstallationOperationState.Running) || !operation.Cancellable) continue;
            try { await installations.CancelAsync(operation.OperationId, ct); }
            catch (InstallationException ex) when (ex.ProblemCode == InstallationProblemCodes.NotCancellable) { }
        }
        return store.GetRun(runId, subject)!;
    }
}
