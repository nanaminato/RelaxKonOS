using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.TaskOrchestration;

internal static class TaskRecoveryChecks
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; }
        var root = Path.Combine(Path.GetTempPath(), "task-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var protection = new EphemeralDataProtectionProvider(); var path = Path.Combine(root, "plans.db"); var store = new TaskPlanStore(path, protection);
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new("sub", "subject")], "fixture"));
            async Task<(TaskRun Run, RecoveryFixture Env, TaskRecoveryService Service)> Setup()
            {
                var env = new RecoveryFixture(store);
                var plan = await new TaskPlanner(RuntimeChangePlanningActions.Create(env.Runtime)).PlanAsync(new("host", "subject"), RuntimeChangePlanningChecks.Intent(RuntimeChange.Update));
                store.Save(plan, "subject");
                var approval = store.RecordApproval(plan.PlanId, plan.PlanDigest, new("subject", "old-session", "host", "old-policy", "linux:0", "fixture", DateTimeOffset.UtcNow.AddMinutes(3)), Guid.NewGuid().ToString("N"));
                var run = store.StartRun(plan.PlanId, approval.ApprovalId, plan.PlanDigest, new("subject", "old-session", "host", "old-policy"), Guid.NewGuid().ToString("N"));
                var dispatch = store.PrepareDispatch(run.RunId, "runtime", new("subject", "old-session", "host", "old-policy"), run.Revision);
                env.Operation = new(Guid.NewGuid(), InstallationServiceId.SingBox, InstallationOperationKind.Upgrade, InstallationOperationState.Interrupted,
                    InstallationStage.Completed, null, null, run.CreatedAt, run.CreatedAt, DateTimeOffset.UtcNow, false);
                run = store.BindDomainOperation(run.RunId, "runtime", "subject", dispatch.RequestDigest, env.Operation.OperationId, 2);
                run = store.RecordReconciliation(run.RunId, "subject", new("runtime", env.Operation, null), run.Revision);
                env.RunId = run.RunId;
                var frozen = TaskPlanJson.ReadInputs<RuntimeTaskFrozenChange>(plan.Semantics.Steps[0].Inputs);
                env.Recovery = new(true, env.Operation.OperationId, frozen.Change, InstallationStage.RollingBack, true,
                    "runtime.switch_failed", "runtime.compensation_failed", frozen.Release?.ReleaseId, frozen.PreviousReleaseId, new(true, true, true));
                store.RevokeApproval(approval.ApprovalId, "subject");
                return (run, env, new(store, env, env, env, env, env, env));
            }
            TaskRecoveryRequest Request(TaskRun run, TaskRecoveryAction action, bool confirmed = true, string? password = "fixture-secret") => new()
            { ExpectedRevision = run.Revision, OperationId = run.Steps[0].DomainOperationId!.Value, Action = action, Confirmed = confirmed, Password = password, AdministratorUsername = "root" };
            async Task Reject(Func<Task> call, string code)
            {
                try { await call(); throw new Exception("Accepted invalid recovery"); }
                catch (TaskApprovalException ex) { Check(ex.ProblemCode == code, code); }
                catch (TaskPlanStoreException ex) { Check(ex.ProblemCode == code, code); }
            }
            var current = await Setup(); var request = Request(current.Run, TaskRecoveryAction.RetryCompensation);
            await Reject(() => current.Service.ExecuteAsync(principal, current.Run.RunId, Request(current.Run, request.Action, false), "unconfirmed", default), "task.confirmation_required");
            Check(current.Env.Authentications == 0 && current.Env.Executions == 0, "confirmation precedes authentication or host changes");
            await Reject(() => current.Service.ExecuteAsync(principal, current.Run.RunId, Request(current.Run, request.Action, password: null), "credentials", default), "task.administrator_authentication_failed");
            var result = await current.Service.ExecuteAsync(principal, current.Run.RunId, request, "recovery", default);
            Check(result.Attempt.ResponseRecorded && result.Attempt.SessionIdentity == "recovery-session" && current.Env.Executions == 1 && current.Env.IntentSeen,
                "fresh recovery authority and intent are durable before original compensation, independent of revoked old approval");
            Check(result.Run.State == TaskRunState.NeedsRecovery, "action acknowledgement does not claim the task recovered");
            var authentications = current.Env.Authentications;
            var replay = await current.Service.ExecuteAsync(principal, current.Run.RunId, request, "recovery", default);
            Check(replay.Attempt.AttemptId == result.Attempt.AttemptId && current.Env.Executions == 1 && current.Env.Authentications == authentications,
                "same-key retry reads original response without reauthenticating or executing twice");
            await Reject(() => current.Service.ExecuteAsync(principal, current.Run.RunId, Request(current.Run, TaskRecoveryAction.Close), "recovery", default), "task.idempotency_conflict");
            Check(new TaskPlanStore(path, protection).GetRecoveryAttempts(current.Run.RunId, "subject").Single() == result.Attempt,
                "reopening preserves exact recovery intent and response without credentials");
            var read = await current.Service.ReadAsync(current.Run.RunId, "subject", default);
            Check(read.Attempts.Length == 1 && current.Env.Executions == 1, "reading recovery history never executes compensation");
            await Reject(() => current.Service.ReadAsync(current.Run.RunId, "other", default), "task.run_not_found");
            foreach (var action in new[] { TaskRecoveryAction.EmergencyStop, TaskRecoveryAction.Close })
            {
                current = await Setup();
                if (action == TaskRecoveryAction.Close)
                {
                    await Reject(() => current.Service.ExecuteAsync(principal, current.Run.RunId, Request(current.Run, action), "close-running", default), "task.recovery_action_unavailable");
                    Check(current.Env.Authentications == 0, "running service cannot be closed before authentication");
                    current.Env.Recovery = current.Env.Recovery with { Service = new(true, false, false) };
                }
                var actionResult = await current.Service.ExecuteAsync(principal, current.Run.RunId, Request(current.Run, action), action.ToString(), default);
                Check(actionResult.Attempt.Action == action && current.Env.Executions == 1 && current.Env.LastAction == action,
                    "stop and acknowledged close are distinct original-operation actions");
            }
            current = await Setup(); current.Env.DuringAuthentication = () => current.Env.PolicyRevision = "changed";
            await Reject(() => current.Service.ExecuteAsync(principal, current.Run.RunId, Request(current.Run, TaskRecoveryAction.RetryCompensation), "policy-change", default), "task.policy_changed");
            Check(current.Env.Executions == 0 && store.GetRecoveryAttempts(current.Run.RunId, "subject").IsEmpty, "authentication-time policy drift leaves no executable intent");
            current = await Setup(); current.Env.DuringExecution = () => current.Env.PolicyRevision = "revoked";
            await Reject(() => current.Service.ExecuteAsync(principal, current.Run.RunId, Request(current.Run, TaskRecoveryAction.RetryCompensation), "stage-revoked", default), "task.execution_authority_invalid");
            Check(current.Env.Executions == 0 && store.GetRecoveryAttempts(current.Run.RunId, "subject") is [{ ResponseRecorded: false }],
                "stage revocation denies side effects while preserving uncertain intent");
            current.Env.DuringExecution = null; current.Env.PolicyRevision = "recovery-policy";
            await current.Service.ExecuteAsync(principal, current.Run.RunId, Request(current.Run, TaskRecoveryAction.RetryCompensation), "stage-revoked", default);
            Check(current.Env.Executions == 0, "unknown response replay never silently retries a host mutation");
            current = await Setup(); current.Env.DuringExecution = () => current.Env.ApproverAvailable = false;
            await Reject(() => current.Service.ExecuteAsync(principal, current.Run.RunId, Request(current.Run, TaskRecoveryAction.RetryCompensation), "approver-revoked", default), "task.approver_unavailable");
            current = await Setup(); current.Env.Recovery = current.Env.Recovery with { OperationId = Guid.NewGuid() };
            await Reject(() => current.Service.ExecuteAsync(principal, current.Run.RunId, Request(current.Run, TaskRecoveryAction.RetryCompensation), "wrong-operation", default), "task.domain_operation_conflict");
            Check(current.Env.Authentications == 0, "another pending operation never reaches authentication");
            current = await Setup();
            var failing = new TaskPlanStore(path, protection, beforeCommit: point => { if (point == TaskPlanCommitPoint.RecoveryIntentWritten) throw new IOException("injected"); });
            var failingService = new TaskRecoveryService(failing, current.Env, current.Env, current.Env, current.Env, current.Env, current.Env);
            await Reject(() => failingService.ExecuteAsync(principal, current.Run.RunId, Request(current.Run, TaskRecoveryAction.RetryCompensation), "disk-failed", default), "task.plan_store_unavailable");
            Check(current.Env.Executions == 0 && new TaskPlanStore(path, protection).GetRecoveryAttempts(current.Run.RunId, "subject").IsEmpty,
                "intent persistence failure prevents any host call and rolls back atomically");
        }
        finally { Directory.Delete(root, true); }
        return count;
    }
}

internal sealed class RecoveryFixture(TaskPlanStore store) : ITaskApprovalPolicyReader, ITaskExecutionPolicyReader,
    ITaskApproverPolicyReader, IHostAdministratorAuthenticator, ITaskReconciliationDomain, ITaskRecoveryDomain
{
    public RuntimeChangeFixture Runtime = new();
    public Guid RunId;
    public InstallationOperationDto Operation = null!;
    public RuntimeRecoverySnapshot Recovery = null!;
    public string PolicyRevision = "recovery-policy";
    public bool ApproverAvailable = true, IntentSeen;
    public int Authentications, Executions;
    public TaskRecoveryAction? LastAction;
    public Action? DuringAuthentication, DuringExecution;
    public TaskExecutionPolicyObservation Read(ClaimsPrincipal principal, string host) => new(new(principal.FindFirst("sub")!.Value, "recovery-session", "host", PolicyRevision), DateTimeOffset.UtcNow.AddMinutes(1));
    public Task<TaskApprovalObservation> ReadAsync(ClaimsPrincipal principal, CancellationToken ct) => Task.FromResult(new TaskApprovalObservation(Read(principal, "host").Identity,
        new("1000", "alice", HostPlatformKind.Linux, "Alice", null), HostAccountPrivilege.StandardUser, DateTimeOffset.UtcNow.AddMinutes(1), Runtime.Installation));
    public void Validate(string identity) { if (!ApproverAvailable || identity != "linux:0") throw new TaskApprovalException("task.approver_unavailable"); }
    public HostAdministratorAuthenticationResult Authenticate(string requester, string? username, string? password)
    { Authentications++; DuringAuthentication?.Invoke(); return password == "fixture-secret" ? new(true, "", "fixture", "linux:0") : new(false, "denied", "none", null); }
    public InstallationOperationDto? Find(Guid runId, TaskDispatchIntent dispatch, string subject) => Operation;
    public Task<RuntimeInstallationSnapshot> ObserveAsync(Guid operationId, CancellationToken ct) => Task.FromResult(Runtime.Installation);
    Task<RuntimeRecoverySnapshot> ITaskRecoveryDomain.ReadAsync(CancellationToken ct) => Task.FromResult(Recovery);
    public async Task<string?> ExecuteAsync(TaskRecoveryAuthorization authority, CancellationToken ct)
    {
        var attempts = store.GetRecoveryAttempts(RunId, "subject");
        IntentSeen = attempts.Any(item => item.AttemptId == authority.AttemptId && !item.ResponseRecorded);
        await authority.ValidateAsync(ct); DuringExecution?.Invoke(); await authority.ValidateAsync(ct);
        Executions++; LastAction = authority.Action; return null;
    }
}
