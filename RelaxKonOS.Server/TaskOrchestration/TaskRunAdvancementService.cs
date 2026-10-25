using System.Security.Claims;
using RelaxKonOS.Protocol.TaskOrchestration;

namespace RelaxKonOS.Server.TaskOrchestration;

/// <summary>One authenticated advancement at a time. No work is scheduled merely by reading a run.</summary>
public sealed class TaskRunAdvancementService(TaskPlanStore store, ITaskExecutionPolicyReader policies,
    IRuntimeTaskPreviewService observations, ITaskStepDispatcher dispatcher, ITaskRunReconciler reconciler)
{
    public async Task<TaskRun> AdvanceAsync(ClaimsPrincipal principal, Guid runId, long expectedRevision, CancellationToken ct)
    {
        var retained = principal.Clone();
        ct.ThrowIfCancellationRequested();
        var subject = retained.FindFirst("sub")?.Value ?? retained.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (retained.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(subject)) throw new TaskApprovalException("task.session_unavailable");
        var run = store.GetRun(runId, subject) ?? throw new TaskPlanStoreException("task.run_not_found", 404);
        if (run.State is not (TaskRunState.Queued or TaskRunState.Running or TaskRunState.Cancelling)) return run;
        if (run.Revision != expectedRevision) throw new TaskPlanStoreException("task.run_revision_conflict", 409);
        // An existing operation's outcome can be recorded without renewing its execution authority.
        if (run.Steps.FirstOrDefault(step => step.State == TaskStepRunState.Running) is { } active)
            return await reconciler.ReconcileAsync(runId, active.StepId, subject, ct);
        if (run.CancellationRequested)
        {
            var pendingDispatch = run.Steps.FirstOrDefault(step => step.State == TaskStepRunState.Dispatching);
            return pendingDispatch is null ? run : await reconciler.ReconcileAsync(runId, pendingDispatch.StepId, subject, ct);
        }
        var live = policies.Read(retained, run.HostId);
        if (live.Identity != new TaskExecutionIdentity(run.CanonicalSubject, run.SessionIdentity, run.HostId, run.PolicyRevision))
            throw new TaskApprovalException("task.execution_authority_invalid");
        if (run.Steps.FirstOrDefault(step => step.State == TaskStepRunState.Dispatching) is { } dispatching)
        {
            await dispatcher.DispatchAsync(retained, runId, dispatching.StepId, ct);
            return store.GetRun(runId, subject)!;
        }
        var plan = store.Get(run.PlanId, subject) ?? throw new TaskPlanStoreException("task.plan_not_found", 404);
        var proposal = plan.Plan.Semantics.Steps.FirstOrDefault(step =>
            run.Steps.Single(item => item.StepId == step.StepId).State == TaskStepRunState.Pending
            && step.DependsOn.All(id => run.Steps.Single(item => item.StepId == id).State is TaskStepRunState.Succeeded or TaskStepRunState.NoOp))
            ?? throw new TaskPlanStoreException("task.step_not_ready", 409);
        ct.ThrowIfCancellationRequested();
        if (proposal.RequiredCapabilities.IsEmpty)
        {
            var intent = new TaskPlanIntent(plan.Plan.Semantics.SourceKind, plan.Plan.Semantics.SourceIdentity,
                plan.Plan.Semantics.InputRevision, [new(proposal.StepId, proposal.ActionId, proposal.SchemaVersion,
                    proposal.ImplementationRevision, proposal.Inputs, [])]);
            var observed = await observations.PreviewAsync(subject, intent, ct);
            var final = policies.Read(retained, run.HostId);
            if (final.Identity != live.Identity) throw new TaskApprovalException("task.policy_changed", 409);
            ct.ThrowIfCancellationRequested();
            return store.RecordReadStep(runId, final.Identity, new(proposal.StepId, observed), run.Revision);
        }
        await dispatcher.DispatchAsync(retained, runId, proposal.StepId, ct);
        return store.GetRun(runId, subject)!;
    }
}
