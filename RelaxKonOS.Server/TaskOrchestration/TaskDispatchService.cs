using System.Security.Claims;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.Installations;

namespace RelaxKonOS.Server.TaskOrchestration;

public interface ITaskStepDispatcher
{
    Task<InstallationOperationDto> DispatchAsync(ClaimsPrincipal principal, Guid runId, string stepId, CancellationToken ct);
}

/// <summary>One explicit step submission, not a scheduler. All dispatch input is rebuilt from the stored plan.</summary>
public sealed class TaskDispatchService(TaskPlanStore store, ITaskApprovalPolicyReader policies,
    ITaskPlanRevalidator revalidator, InstallationCoordinator installations, TaskStepAuthorizationService authorization) : ITaskStepDispatcher
{
    public async Task<InstallationOperationDto> DispatchAsync(ClaimsPrincipal principal, Guid runId, string stepId, CancellationToken ct)
    {
        var retained = principal.Clone();
        var before = await policies.ReadAsync(retained, ct);
        var run = store.GetRun(runId, before.Identity.CanonicalSubject) ?? throw new TaskApprovalException("task.run_not_found", 404);
        if (before.Identity != new TaskExecutionIdentity(run.CanonicalSubject, run.SessionIdentity, run.HostId, run.PolicyRevision))
            throw new TaskApprovalException("task.execution_authority_invalid");
        var dispatch = store.GetDispatch(runId, stepId, run.CanonicalSubject);
        if (dispatch is not null)
        {
            var request = TaskPlanJson.ReadInputs<ManagedRuntimeInstallationRequest>(dispatch.Request);
            var existing = installations.FindByKey(dispatch.Service, dispatch.Kind, dispatch.Request, run.CanonicalSubject, (request.Plan ?? throw new TaskApprovalException("task.dispatch_invalid")).Request.IdempotencyKey);
            if (existing is not null)
            {
                if (existing.TaskOwner != new InstallationTaskOwner(runId, stepId, dispatch.RequestDigest))
                    throw new TaskApprovalException("task.domain_operation_conflict", 409);
                store.BindDomainOperation(runId, stepId, run.CanonicalSubject, dispatch.RequestDigest, existing.Operation.OperationId, run.Revision);
                return existing.Operation; // An accepted original key is never started again, even after interruption.
            }
        }
        var plan = store.Get(run.PlanId, run.CanonicalSubject) ?? throw new TaskApprovalException("task.plan_not_found", 404);
        await revalidator.ValidateAsync(plan.Plan, ct);
        var after = await policies.ReadAsync(retained, ct);
        if (after.Identity != before.Identity) throw new TaskApprovalException("task.policy_changed", 409);
        ct.ThrowIfCancellationRequested();
        dispatch = store.PrepareDispatch(runId, stepId, after.Identity, run.Revision);
        var frozen = TaskPlanJson.ReadInputs<ManagedRuntimeInstallationRequest>(dispatch.Request);
        var binding = new TaskInstallationExecutionBinding(new(runId, stepId, dispatch.RequestDigest),
            () => store.ValidateDispatchAcceptance(runId, stepId, after.Identity, dispatch.RequestDigest), operationId =>
        {
            var current = store.GetRun(runId, run.CanonicalSubject) ?? throw new TaskApprovalException("task.run_not_found", 404);
            store.BindDomainOperation(runId, stepId, run.CanonicalSubject, dispatch.RequestDigest, operationId, current.Revision);
            return authorization.Bind(retained, runId, stepId, operationId, dispatch.RequestDigest);
        });
        return installations.StartTask(dispatch.Kind, dispatch.Request, run.CanonicalSubject, (frozen.Plan ?? throw new TaskApprovalException("task.dispatch_invalid")).Request.IdempotencyKey, binding);
    }
}
