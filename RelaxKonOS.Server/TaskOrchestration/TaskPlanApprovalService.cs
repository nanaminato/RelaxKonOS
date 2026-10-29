using System.Collections.Immutable;
using System.Security.Claims;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.TaskOrchestration;

public interface ITaskPlanRevalidator
{
    Task ValidateAsync(TaskPlan plan, CancellationToken ct);
}

/// <summary>Re-observes the exact selection; never replaces the approved plan with a newly resolved release.</summary>
public sealed class RuntimeTaskPlanRevalidator(IRuntimeTaskChangePlanningService planning) : ITaskPlanRevalidator
{
    public async Task ValidateAsync(TaskPlan plan, CancellationToken ct)
    {
        TaskPlanStore.ProposalCapabilities(plan);
        var semantics = plan.Semantics;
        var intent = new TaskPlanIntent(semantics.SourceKind, semantics.SourceIdentity, semantics.InputRevision,
            semantics.Steps.Select(step => new TaskStepIntent(step.StepId, step.ActionId, step.SchemaVersion,
                step.ImplementationRevision, step.RequiredCapabilities.IsEmpty ? step.Inputs
                    : TaskPlanJson.FreezeInputs(TaskPlanJson.ReadInputs<RuntimeTaskFrozenChange>(step.Inputs).Selection),
                step.DependsOn)).ToImmutableArray());
        var current = await planning.PlanAsync(semantics.CanonicalSubject, intent, ct);
        // Disk free bytes are observation evidence, not a different release or requested effect.
        // Keep all other dependency fields and semantics exact; the fresh planner must still admit the requirement.
        var steps = current.Semantics.Steps.Select(step =>
        {
            var original = semantics.Steps.SingleOrDefault(item => item.StepId == step.StepId);
            if (original is null || step.RequiredCapabilities.IsEmpty || original.RequiredCapabilities.IsEmpty) return step;
            var frozen = TaskPlanJson.ReadInputs<RuntimeTaskFrozenChange>(original.Inputs);
            var observed = TaskPlanJson.ReadInputs<RuntimeTaskFrozenChange>(step.Inputs);
            if (observed.Dependencies is null || frozen.Dependencies is null) return step;
            var evidence = observed.Dependencies.Observations.Select(item =>
            {
                var prior = frozen.Dependencies.Observations.SingleOrDefault(old => old.Kind == item.Kind && old.Identity == item.Identity);
                return item.Kind == RuntimeDependencyKind.DiskSpace && prior is not null
                    && item.State == RuntimeDependencyState.Satisfied && item.RequiredBytes is > 0
                    && item.AvailableBytes >= item.RequiredBytes
                    ? item with { AvailableBytes = prior.AvailableBytes } : item;
            }).ToArray();
            return step with { Inputs = TaskPlanJson.FreezeInputs(observed with { Dependencies = new(evidence) }) };
        }).ToImmutableArray();
        if (TaskPlanCanonicalizer.Digest(current.Semantics with { Steps = steps }) != plan.PlanDigest)
            throw new TaskApprovalException("task.plan_changed", 409);
    }
}

/// <summary>Internal approval composition. No HTTP exposure until per-step dispatch authorization exists.</summary>
public sealed class TaskPlanApprovalService(TaskPlanStore store, ITaskApprovalPolicyReader policy,
    ITaskPlanRevalidator revalidator, IHostAdministratorAuthenticator administrators, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<TaskPlanApproval> ApproveAsync(ClaimsPrincipal principal, Guid planId, string digest,
        bool confirmed, string requestKey, string? administratorUsername, string? password, CancellationToken ct)
    {
        if (!confirmed) throw new TaskApprovalException("task.confirmation_required", 400);
        var before = await policy.ReadAsync(principal, ct);
        var plan = RequirePlan(planId, digest, before.Identity);
        TaskPlanStore.ProposalCapabilities(plan);
        await revalidator.ValidateAsync(plan, ct);
        string approver, method;
        if (before.RequesterPrivilege is HostAccountPrivilege.HostAdministrator or HostAccountPrivilege.HostRoot)
        {
            approver = CanonicalIdentity(before.Requester.Platform, before.Requester.Uid);
            method = before.RequesterPrivilege == HostAccountPrivilege.HostRoot ? "current-host-root" : "current-host-administrator";
        }
        else
        {
            ct.ThrowIfCancellationRequested();
            var authentication = administrators.Authenticate(before.Requester.Username, administratorUsername, password);
            if (!authentication.Succeeded || string.IsNullOrWhiteSpace(authentication.AdministratorIdentity)
                || string.IsNullOrWhiteSpace(authentication.AuthenticationMethod)
                || !authentication.AdministratorIdentity.StartsWith(PlatformPrefix(before.Requester.Platform), StringComparison.Ordinal)
                || authentication.AdministratorIdentity.Length <= PlatformPrefix(before.Requester.Platform).Length)
                throw new TaskApprovalException("task.administrator_authentication_failed");
            approver = authentication.AdministratorIdentity;
            method = authentication.AuthenticationMethod;
        }
        ct.ThrowIfCancellationRequested();
        await revalidator.ValidateAsync(plan, ct);
        var after = await policy.ReadAsync(principal, ct);
        if (after.Identity != before.Identity || after.Requester != before.Requester || after.RequesterPrivilege != before.RequesterPrivilege)
            throw new TaskApprovalException("task.policy_changed", 409);
        ValidateFinalInstallation(plan, after.Installation);
        ct.ThrowIfCancellationRequested();
        var until = before.ValidUntil < after.ValidUntil ? before.ValidUntil : after.ValidUntil;
        if (until <= _clock.GetUtcNow()) throw new TaskApprovalException("task.session_unavailable");
        var identity = after.Identity;
        return store.RecordApproval(planId, digest, new(identity.CanonicalSubject, identity.SessionIdentity,
            identity.HostId, identity.PolicyRevision, approver, method, until), requestKey);
    }

    private static void ValidateFinalInstallation(TaskPlan plan, RuntimeInstallationSnapshot snapshot)
    {
        foreach (var step in plan.Semantics.Steps.Where(item => !item.RequiredCapabilities.IsEmpty))
        {
            var frozen = TaskPlanJson.ReadInputs<RuntimeTaskFrozenChange>(step.Inputs);
            var state = snapshot.Installation;
            if (state.RuntimeId != frozen.RuntimeId || state.HostId != frozen.Selection.HostId
                || state.InstallationId != frozen.Selection.InstallationId || state.StateRevision != frozen.Selection.ExpectedStateRevision
                || state.ActiveReleaseId != frozen.PreviousReleaseId || snapshot.MeasuredVersion != frozen.CurrentVersion
                || snapshot.ConfigurationIdentity != frozen.ConfigurationIdentity || snapshot.PendingOperationId is not null
                || state.RecoveryRequired || state.Ownership is RuntimeOwnership.External or RuntimeOwnership.Unknown)
                throw new TaskApprovalException("task.plan_changed", 409);
        }
    }

    private TaskPlan RequirePlan(Guid id, string digest, TaskExecutionIdentity identity)
    {
        var record = store.Get(id, identity.CanonicalSubject) ?? throw new TaskApprovalException("task.plan_not_found", 404);
        if (record.Plan.PlanDigest != digest) throw new TaskApprovalException("task.plan_digest_conflict", 409);
        if (record.Validity != TaskPlanValidity.Resolved || record.ExpiresAt <= _clock.GetUtcNow())
            throw new TaskApprovalException("task.plan_expired", 409);
        if (record.Plan.Semantics.HostId != identity.HostId) throw new TaskApprovalException("task.policy_changed", 409);
        return record.Plan;
    }
    private static string PlatformPrefix(HostPlatformKind platform) => platform switch
    { HostPlatformKind.Windows => "windows:", HostPlatformKind.Linux => "linux:", _ => throw new TaskApprovalException("task.identity_unavailable", 503) };
    private static string CanonicalIdentity(HostPlatformKind platform, string uid) =>
        !string.IsNullOrWhiteSpace(uid) ? PlatformPrefix(platform) + uid : throw new TaskApprovalException("task.identity_unavailable", 503);
}
