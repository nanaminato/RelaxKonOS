using System.Security.Claims;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Installations;
using System.Text.Json;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.TaskOrchestration;

public interface ITaskApproverPolicyReader
{
    void Validate(string canonicalIdentity);
}

/// <summary>Rechecks the approving account's immutable identity and current host administrator membership.</summary>
public sealed class TaskApproverPolicyReader(IServiceScopeFactory scopes) : ITaskApproverPolicyReader
{
    public void Validate(string canonicalIdentity)
    {
        var separator = canonicalIdentity.IndexOf(':');
        if (separator <= 0 || separator == canonicalIdentity.Length - 1) throw new TaskApprovalException("task.approver_unavailable");
        var platform = canonicalIdentity[..separator] switch
        { "linux" => HostPlatformKind.Linux, "windows" => HostPlatformKind.Windows, _ => throw new TaskApprovalException("task.approver_unavailable") };
        using var scope = scopes.CreateScope();
        var uid = canonicalIdentity[(separator + 1)..];
        var lookup = scope.ServiceProvider.GetRequiredService<IIdentityProvider>().LookupIdentity(uid);
        if (lookup.Status != IdentityLookupStatus.Found || lookup.Identity is not { } identity || identity.Uid != uid || identity.Platform != platform
            || scope.ServiceProvider.GetRequiredService<IHostAccountPrivilegeService>().Classify(identity)
                is not (HostAccountPrivilege.HostAdministrator or HostAccountPrivilege.HostRoot))
            throw new TaskApprovalException("task.approver_unavailable");
    }
}

/// <summary>Process-local, immutable binding. It is neither serializable authorization nor a generic elevation grant.</summary>
public sealed class TaskStepExecutionAuthorization
{
    private readonly Func<CancellationToken, DateTimeOffset> _validate;
    private readonly JsonElement _request;
    public Guid OperationId { get; }
    internal TaskStepExecutionAuthorization(Guid operationId, Func<CancellationToken, DateTimeOffset> validate, JsonElement request)
    { OperationId = operationId; _validate = validate; _request = request.Clone(); }
    internal bool MatchesPlan(RuntimeChangePlan plan) => TaskPlanCanonicalizer.Canonicalize(_request)
        .SequenceEqual(TaskPlanCanonicalizer.Canonicalize(TaskPlanJson.FreezeInputs(new ManagedRuntimeInstallationRequest(true, plan))));
    public Task<DateTimeOffset> ValidateAsync(CancellationToken ct) => Task.FromResult(_validate(ct));
}

/// <summary>Validates every stage without querying runtime state or reacquiring the runtime's held domain gate.</summary>
public sealed class TaskStepAuthorizationService(TaskPlanStore store, ITaskExecutionPolicyReader policies,
    ITaskApproverPolicyReader approvers, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public TaskStepExecutionAuthorization Bind(ClaimsPrincipal principal, Guid runId, string stepId, Guid operationId, string requestDigest)
    {
        var retained = principal.Clone();
        var subject = retained.FindFirst("sub")?.Value ?? retained.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(subject)) throw new TaskApprovalException("task.session_unavailable");
        var bound = false;
        DateTimeOffset Check(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var run = store.GetRun(runId, subject) ?? throw new TaskApprovalException("task.run_not_found", 404);
            var live = policies.Read(retained, run.HostId);
            var approval = store.GetApproval(run.ApprovalId, subject) ?? throw new TaskApprovalException("task.approval_unavailable");
            var plan = store.Get(run.PlanId, subject) ?? throw new TaskApprovalException("task.plan_not_found", 404);
            var step = run.Steps.SingleOrDefault(item => item.StepId == stepId);
            var dispatch = store.GetDispatch(runId, stepId, subject);
            if (operationId == Guid.Empty || run.State is not (TaskRunState.Running or TaskRunState.Cancelling) || !bound && run.CancellationRequested
                || step is not { State: TaskStepRunState.Running } || step.DomainOperationId != operationId
                || step.RequestDigest != requestDigest || dispatch is null || dispatch.RequestDigest != requestDigest
                || dispatch.AdapterRevision != "sing-box-dispatch-1"
                || approval.State != TaskApprovalState.Consumed || approval.RunId != runId || approval.PlanId != run.PlanId
                || approval.PlanDigest != run.PlanDigest || plan.Plan.PlanDigest != run.PlanDigest || plan.Validity != TaskPlanValidity.Resolved
                || live.Identity != new TaskExecutionIdentity(run.CanonicalSubject, run.SessionIdentity, run.HostId, run.PolicyRevision)
                || approval.PolicyRevision != live.Identity.PolicyRevision
                || approval.Capabilities is not [{ Capability: "SingBoxInstall", Target: "singbox" }])
                throw new TaskApprovalException("task.execution_authority_invalid");
            approvers.Validate(approval.ApproverIdentity);
            var finalPolicy = policies.Read(retained, run.HostId);
            // OS membership/policy reads can yield to external work. Re-read durable revocation and run state afterwards.
            if (store.GetApproval(run.ApprovalId, subject)?.Revision != approval.Revision
                || store.GetRun(runId, subject)?.Revision != run.Revision || finalPolicy.Identity != live.Identity)
                throw new TaskApprovalException("task.execution_authority_invalid");
            var now = _clock.GetUtcNow();
            var until = now.AddSeconds(10);
            if (approval.RunAuthorizationExpiresAt < until) until = approval.RunAuthorizationExpiresAt;
            if (live.ValidUntil < until) until = live.ValidUntil;
            if (finalPolicy.ValidUntil < until) until = finalPolicy.ValidUntil;
            if (until <= now) throw new TaskApprovalException("task.execution_authority_expired");
            ct.ThrowIfCancellationRequested();
            return until;
        }
        Check(default);
        bound = true;
        return new(operationId, Check, store.GetDispatch(runId, stepId, subject)!.Request);
    }
}
