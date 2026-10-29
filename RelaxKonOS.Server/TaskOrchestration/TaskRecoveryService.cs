using System.Security.Claims;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.TaskOrchestration;

/// <summary>Only compensation of the named transaction, emergency stop, or acknowledged close. No forward execution.</summary>
public interface ITaskRecoveryDomain
{
    Task<RuntimeRecoverySnapshot> ReadAsync(CancellationToken ct);
    Task<string?> ExecuteAsync(TaskRecoveryAuthorization authority, CancellationToken ct);
}
public sealed class SingBoxTaskRecoveryDomain(SingBoxRuntimeService runtime) : ITaskRecoveryDomain
{
    public Task<RuntimeRecoverySnapshot> ReadAsync(CancellationToken ct) => runtime.RecoveryAsync(ct);
    public async Task<string?> ExecuteAsync(TaskRecoveryAuthorization authority, CancellationToken ct)
    {
        switch (authority.Action)
        {
            case TaskRecoveryAction.RetryCompensation:
                return (await runtime.RecoverTaskAsync(authority, ct))?.ProblemCode ?? null;
            case TaskRecoveryAction.EmergencyStop:
                return (await runtime.StopTaskRecoveryAsync(authority, ct)).ServiceProblem;
            case TaskRecoveryAction.Close:
                return (await runtime.CloseTaskRecoveryAsync(authority, ct)).ProblemCode;
            default: throw new TaskApprovalException("task.recovery_request_invalid", 400);
        }
    }
}

/// <summary>Short-lived, process-local authorization for exactly one recovery intent. Never a generic elevation grant.</summary>
public sealed class TaskRecoveryAuthorization
{
    public Guid AttemptId { get; }
    public Guid OperationId { get; }
    public TaskRecoveryAction Action { get; }
    private readonly Func<CancellationToken, DateTimeOffset> _validate;
    internal TaskRecoveryAuthorization(TaskRecoveryAttempt attempt, Func<CancellationToken, DateTimeOffset> validate)
    { AttemptId = attempt.AttemptId; OperationId = attempt.OperationId; Action = attempt.Action; _validate = validate; }
    public Task<DateTimeOffset> ValidateAsync(CancellationToken ct) => Task.FromResult(_validate(ct));
}

public sealed class TaskRecoveryService(TaskPlanStore store, ITaskApprovalPolicyReader approvals, ITaskExecutionPolicyReader policies,
    ITaskApproverPolicyReader approvers, IHostAdministratorAuthenticator administrators, ITaskReconciliationDomain operations,
    ITaskRecoveryDomain domain, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public async Task<TaskRecoveryView> ReadAsync(Guid runId, string subject, CancellationToken ct)
    {
        var run = store.GetRun(runId, subject) ?? throw new TaskPlanStoreException("task.run_not_found", 404);
        var attempts = store.GetRecoveryAttempts(runId, subject);
        var recovery = run.State == TaskRunState.NeedsRecovery ? await domain.ReadAsync(ct)
            : new RuntimeRecoverySnapshot(false, null, null, null, false, null, null, null, null);
        if (recovery.Required && !run.Steps.Any(step => step.State == TaskStepRunState.NeedsRecovery && step.DomainOperationId == recovery.OperationId))
            throw new TaskPlanStoreException("task.domain_operation_conflict", 409);
        return new(run, recovery, attempts);
    }

    public async Task<TaskRecoveryResponse> ExecuteAsync(ClaimsPrincipal principal, Guid runId, TaskRecoveryRequest request, string key, CancellationToken ct)
    {
        if (!request.Confirmed) throw new TaskApprovalException("task.confirmation_required", 400);
        if (!Enum.IsDefined(request.Action) || request.OperationId == Guid.Empty || request.ExpectedRevision < 1)
            throw new TaskApprovalException("task.recovery_request_invalid", 400);
        var subject = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(subject)) throw new TaskApprovalException("task.permission_denied");
        // A response-lost retry only retrieves its durable identity. It cannot renew authority or call the host twice.
        if (store.FindRecoveryAttempt(runId, subject, request, key) is { } previous)
            return new(store.GetRun(runId, subject)!, previous);
        var before = await approvals.ReadAsync(principal, ct);
        var run = RequireRun(runId, request, before);
        var step = TaskPlanStore.RequireRecoveryStep(run, request.OperationId);
        var dispatch = store.GetDispatch(runId, step.StepId, subject) ?? throw new TaskPlanStoreException("task.step_not_ready", 409);
        var operation = operations.Find(runId, dispatch, subject);
        if (operation is null || operation.OperationId != request.OperationId || operation.Service != dispatch.Service || operation.Kind != dispatch.Kind)
            throw new TaskPlanStoreException("task.domain_operation_conflict", 409);
        if (operation.State is InstallationOperationState.Queued or InstallationOperationState.Running)
            throw new TaskPlanStoreException("task.domain_operation_active", 409);
        var frozen = TaskPlanJson.ReadInputs<RuntimeTaskFrozenChange>(store.Get(run.PlanId, subject)!.Plan.Semantics.Steps.Single(item => item.StepId == step.StepId).Inputs);
        RequireHost(before, run, frozen);
        RequireAction(await domain.ReadAsync(ct), request, frozen);
        string approver, method;
        var prefix = before.Requester.Platform switch
        { HostPlatformKind.Linux => "linux:", HostPlatformKind.Windows => "windows:", _ => throw new TaskApprovalException("task.identity_unavailable", 503) };
        if (before.RequesterPrivilege is HostAccountPrivilege.HostAdministrator or HostAccountPrivilege.HostRoot)
        {
            if (string.IsNullOrWhiteSpace(before.Requester.Uid)) throw new TaskApprovalException("task.identity_unavailable", 503);
            approver = prefix + before.Requester.Uid; method = "current-host-administrator";
        }
        else
        {
            var authentication = administrators.Authenticate(before.Requester.Username, request.AdministratorUsername, request.Password);
            if (!authentication.Succeeded || authentication.AdministratorIdentity is not { } identity || !identity.StartsWith(prefix, StringComparison.Ordinal)
                || identity.Length <= prefix.Length || string.IsNullOrWhiteSpace(authentication.AuthenticationMethod))
                throw new TaskApprovalException("task.administrator_authentication_failed");
            approver = identity; method = authentication.AuthenticationMethod;
        }
        var after = await approvals.ReadAsync(principal, ct);
        if (after.Identity != before.Identity || after.Requester != before.Requester || after.RequesterPrivilege != before.RequesterPrivilege)
            throw new TaskApprovalException("task.policy_changed", 409);
        RequireRun(runId, request, after); RequireHost(after, run, frozen);
        RequireAction(await domain.ReadAsync(ct), request, frozen);
        ct.ThrowIfCancellationRequested();
        var until = before.ValidUntil < after.ValidUntil ? before.ValidUntil : after.ValidUntil;
        var (attempt, created) = store.RecordRecoveryIntent(runId, request, new(after.Identity.CanonicalSubject, after.Identity.SessionIdentity,
            after.Identity.HostId, after.Identity.PolicyRevision, approver, method, until), key);
        if (!created) return new(store.GetRun(runId, subject)!, attempt);
        var authority = Bind(principal, attempt);
        string? problem;
        try { problem = await domain.ExecuteAsync(authority, ct); }
        catch (RuntimeLifecycleException ex)
        {
            store.RecordRecoveryResponse(runId, subject, attempt.AttemptId, SafeProblem(ex.ProblemCode));
            throw;
        }
        attempt = store.RecordRecoveryResponse(runId, subject, attempt.AttemptId, SafeProblem(problem));
        return new(store.GetRun(runId, subject)!, attempt);
    }

    private TaskRun RequireRun(Guid runId, TaskRecoveryRequest request, TaskApprovalObservation live)
    {
        var run = store.GetRun(runId, live.Identity.CanonicalSubject) ?? throw new TaskPlanStoreException("task.run_not_found", 404);
        if (run.Revision != request.ExpectedRevision) throw new TaskPlanStoreException("task.run_revision_conflict", 409);
        TaskPlanStore.RequireRecoveryStep(run, request.OperationId);
        if (run.HostId != live.Identity.HostId) throw new TaskApprovalException("task.policy_changed", 409);
        return run;
    }
    private static void RequireHost(TaskApprovalObservation live, TaskRun run, RuntimeTaskFrozenChange frozen)
    {
        var state = live.Installation.Installation;
        if (state.HostId != run.HostId || state.RuntimeId != frozen.RuntimeId || state.InstallationId != frozen.Selection.InstallationId)
            throw new TaskApprovalException("task.domain_operation_conflict", 409);
    }
    private static void RequireAction(RuntimeRecoverySnapshot recovery, TaskRecoveryRequest request, RuntimeTaskFrozenChange frozen)
    {
        if (!recovery.Required || recovery.OperationId != request.OperationId || recovery.Change != frozen.Change
            || recovery.TargetReleaseId != frozen.Release?.ReleaseId || recovery.PreviousReleaseId != frozen.PreviousReleaseId)
            throw new TaskApprovalException("task.domain_operation_conflict", 409);
        if (request.Action == TaskRecoveryAction.EmergencyStop && recovery.Service?.Running != true
            || request.Action == TaskRecoveryAction.Close && (!recovery.NeedsOperatorDecision || recovery.Service?.Running != false || recovery.ServiceProblem is not null))
            throw new TaskApprovalException("task.recovery_action_unavailable", 409);
    }
    private TaskRecoveryAuthorization Bind(ClaimsPrincipal principal, TaskRecoveryAttempt attempt)
    {
        var retained = principal.Clone();
        DateTimeOffset Check(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var live = policies.Read(retained, attempt.HostId);
            if (live.Identity != new TaskExecutionIdentity(attempt.CanonicalSubject, attempt.SessionIdentity, attempt.HostId, attempt.PolicyRevision))
                throw new TaskApprovalException("task.execution_authority_invalid");
            var run = store.GetRun(attempt.RunId, attempt.CanonicalSubject) ?? throw new TaskApprovalException("task.run_not_found", 404);
            var step = TaskPlanStore.RequireRecoveryStep(run, attempt.OperationId);
        if (step.StepId != attempt.StepId || store.GetRecoveryAttempts(run.RunId, attempt.CanonicalSubject).Single(item => item.AttemptId == attempt.AttemptId) != attempt)
                throw new TaskApprovalException("task.execution_authority_invalid");
            approvers.Validate(attempt.ApproverIdentity);
            var final = policies.Read(retained, attempt.HostId);
            if (final.Identity != live.Identity) throw new TaskApprovalException("task.execution_authority_invalid");
            var now = _clock.GetUtcNow(); var until = now.AddSeconds(10);
            foreach (var limit in new[] { attempt.ValidUntil, live.ValidUntil, final.ValidUntil }) if (limit < until) until = limit;
            if (until <= now) throw new TaskApprovalException("task.execution_authority_expired");
            return until;
        }
        Check(default); return new(attempt, Check);
    }
    private static string? SafeProblem(string? code) => code is null ? null : code.StartsWith("runtime.", StringComparison.Ordinal)
        && code.Length <= 128 && code.All(c => char.IsAsciiLetterLower(c) || c is '.' or '_') ? code : "runtime.helper_response_invalid";
}
