using System.Security.Claims;
using RelaxKonOS.Protocol.TaskOrchestration;

namespace RelaxKonOS.Server.TaskOrchestration;

public sealed class TaskRunStartService(TaskPlanStore store, ITaskApprovalPolicyReader policies, ITaskPlanRevalidator revalidator)
{
    public async Task<TaskRun> StartAsync(ClaimsPrincipal principal, Guid planId, StartTaskRunRequest request, string key, CancellationToken ct)
    {
        var before = await policies.ReadAsync(principal, ct);
        var approval = store.GetApproval(request.ApprovalId, before.Identity.CanonicalSubject)
            ?? throw new TaskApprovalException("task.approval_not_found", 404);
        if (approval.RunId is not null)
            return store.StartRun(planId, request.ApprovalId, request.PlanDigest, before.Identity, key);
        var plan = store.Get(planId, before.Identity.CanonicalSubject) ?? throw new TaskApprovalException("task.plan_not_found", 404);
        if (plan.Plan.PlanDigest != request.PlanDigest) throw new TaskApprovalException("task.plan_digest_conflict", 409);
        await revalidator.ValidateAsync(plan.Plan, ct);
        var after = await policies.ReadAsync(principal, ct);
        if (after.Identity != before.Identity) throw new TaskApprovalException("task.policy_changed", 409);
        ct.ThrowIfCancellationRequested();
        return store.StartRun(planId, request.ApprovalId, request.PlanDigest, after.Identity, key);
    }
}
