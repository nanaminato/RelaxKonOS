using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.TaskOrchestration;

internal static class TaskDispatchServiceChecks
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; }
        var root = Path.Combine(Path.GetTempPath(), "task-dispatch-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var host = Host.CreateDefaultBuilder().UseContentRoot(root).Build();
            var plans = new TaskPlanStore(Path.Combine(root, "plans.db"), new EphemeralDataProtectionProvider());
            var ledger = new InstallationOperationStore(host.Services.GetRequiredService<IHostEnvironment>());
            var domain = new Domain();
            var coordinator = new InstallationCoordinator(ledger, [domain], host.Services.GetRequiredService<IHostApplicationLifetime>());
            await coordinator.StartAsync(default);
            var runtime = new RuntimeChangeFixture();
            var planning = new Planning(runtime);
            var plan = await planning.PlanAsync("subject", RuntimeChangePlanningChecks.Intent(RuntimeChange.Update), default);
            plans.Save(plan, "subject");
            var live = new TaskExecutionIdentity("subject", "session", "host", "policy");
            var grant = plans.RecordApproval(plan.PlanId, plan.PlanDigest, new("subject", "session", "host", "policy",
                "linux:0", "fixture", DateTimeOffset.UtcNow.AddMinutes(5)), "approve");
            var run = plans.StartRun(plan.PlanId, grant.ApprovalId, plan.PlanDigest, live, "start");
            var policies = new Policies(live);
            var authority = new TaskStepAuthorizationService(plans, policies, new Approvers());
            var dispatcher = new TaskDispatchService(plans, policies, new RuntimeTaskPlanRevalidator(planning), coordinator, authority);
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "subject")], "fixture"));
            policies.Denied = true;
            try { await dispatcher.DispatchAsync(principal, run.RunId, "runtime", default); throw new Exception("Dispatched without business permission"); }
            catch (TaskApprovalException) { Check(ledger.Read().Length == 0 && domain.Starts == 0, "business denial precedes journal/domain acceptance and worker execution"); }
            policies.Denied = false;
            var operation = await dispatcher.DispatchAsync(principal, run.RunId, "runtime", default);
            await domain.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var dispatch = plans.GetDispatch(run.RunId, "runtime", "subject")!;
            Check(ledger.Read().Single().TaskOwner == new InstallationTaskOwner(run.RunId, "runtime", dispatch.RequestDigest)
                && plans.GetRun(run.RunId, "subject")?.Steps.Single().DomainOperationId == operation.OperationId,
                "domain acceptance durably records task ownership and worker binds the same operation before domain code");
            Check(domain.Authority?.OperationId == operation.OperationId && domain.Starts == 1,
                "worker receives task authorization through the typed progress boundary");
            var retry = await dispatcher.DispatchAsync(principal, run.RunId, "runtime", default);
            Check(retry.OperationId == operation.OperationId && domain.Starts == 1 && ledger.Read().Length == 1,
                "retries return the accepted operation without creating or restarting a worker");
            var request = TaskPlanJson.ReadInputs<ManagedRuntimeInstallationRequest>(dispatch.Request);
            try { coordinator.Start(dispatch.Service, dispatch.Kind, dispatch.Request, "subject", request.Plan.Request.IdempotencyKey); throw new Exception("Retargeted task operation to generic execution"); }
            catch (InstallationException ex) { Check(ex.ProblemCode == InstallationProblemCodes.IdempotencyConflict, "ordinary installation start cannot adopt a task-owned idempotency key"); }
            policies.Denied = true;
            try { await domain.Authority!.ValidateAsync(default); throw new Exception("Worker retained revoked policy"); }
            catch (TaskApprovalException) { Check(true, "the retained worker context still performs fresh policy checks"); }
            policies.Denied = false;
            domain.Release.TrySetResult();
            await coordinator.StopAsync(default);
            Check(coordinator.Get(operation.OperationId)!.Operation.State == InstallationOperationState.Succeeded,
                "domain worker outcome uses the existing durable installation ledger");
            ledger.Update(operation.OperationId, value => value with { State = InstallationOperationState.Running, CompletedAt = null }, "fixture-interrupted");
            var reopened = new InstallationOperationStore(host.Services.GetRequiredService<IHostEnvironment>());
            var restarted = new InstallationCoordinator(reopened, [domain], host.Services.GetRequiredService<IHostApplicationLifetime>());
            await restarted.StartAsync(default);
            Check(domain.Recoveries == 0 && restarted.Get(operation.OperationId)!.Operation.State == InstallationOperationState.Interrupted
                && restarted.Get(operation.OperationId)!.TaskOwner is not null,
                "restart preserves task ownership and never calls ordinary privileged domain recovery");
            domain.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            domain.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var nextPlan = plan with { PlanId = Guid.NewGuid() }; plans.Save(nextPlan, "subject");
            var nextApproval = plans.RecordApproval(nextPlan.PlanId, nextPlan.PlanDigest, new("subject", "session", "host", "policy",
                "linux:0", "fixture", DateTimeOffset.UtcNow.AddMinutes(5)), "cancel-approve");
            var nextRun = plans.StartRun(nextPlan.PlanId, nextApproval.ApprovalId, nextPlan.PlanDigest, live, "cancel-start");
            var nextDispatcher = new TaskDispatchService(plans, policies, new RuntimeTaskPlanRevalidator(planning), restarted, authority);
            var nextOperation = await nextDispatcher.DispatchAsync(principal, nextRun.RunId, "runtime", default);
            await domain.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            domain.DuringCancel = () => Check(plans.GetRun(nextRun.RunId, "subject")?.CancellationRequested == true,
                "cancel intent is durable before the coordinator delivers it to the domain");
            var cancellation = new TaskRunCancellationService(plans, policies, new SingBoxTaskReconciliationDomain(restarted, null!), restarted);
            reopened.Update(nextOperation.OperationId, value => value with { Cancellable = false }, "fixture-commit-boundary");
            var cancelling = await cancellation.RequestAsync(principal, nextRun.RunId, 3, default);
            Check(cancelling.State == TaskRunState.Cancelling && !domain.CancellationReceived,
                "an uncancellable domain stage remains cancelling without a false delivery or terminal claim");
            reopened.Update(nextOperation.OperationId, value => value with { Cancellable = true }, "fixture-safe-boundary");
            var retriedCancellation = await cancellation.RequestAsync(principal, nextRun.RunId, 3, default);
            Check(retriedCancellation.Revision == cancelling.Revision, "delivery retry does not append another task cancellation event");
            Check(cancelling.State == TaskRunState.Cancelling && domain.CancellationReceived,
                "task cancellation targets the original task-owned operation and waits for its receipt");
            var unacceptedPlan = plan with { PlanId = Guid.NewGuid() }; plans.Save(unacceptedPlan, "subject");
            var unacceptedApproval = plans.RecordApproval(unacceptedPlan.PlanId, unacceptedPlan.PlanDigest,
                new("subject", "session", "host", "policy", "linux:0", "fixture", DateTimeOffset.UtcNow.AddMinutes(5)), "unaccepted-approve");
            var unaccepted = plans.StartRun(unacceptedPlan.PlanId, unacceptedApproval.ApprovalId, unacceptedPlan.PlanDigest, live, "unaccepted-start");
            domain.DuringValidate = () => cancellation.RequestAsync(principal, unaccepted.RunId, 2, default).GetAwaiter().GetResult();
            try { await nextDispatcher.DispatchAsync(principal, unaccepted.RunId, "runtime", default); throw new Exception("Accepted after fenced cancellation"); }
            catch (TaskPlanStoreException ex) { Check(ex.ProblemCode == "task.step_not_ready", "admission rechecks durable authority after a cancellation closes the original intent"); }
            Check(plans.GetRun(unaccepted.RunId, "subject") is { State: TaskRunState.Cancelled,
                Steps: [{ DomainOperationId: null, ProblemCode: "task.cancelled_before_acceptance" }] }
                && domain.Starts == 2 && reopened.Read().Length == 2, "the cancellation/admission race creates neither a third domain operation nor a worker");
            await restarted.StopAsync(default);
            var cancelledOperation = restarted.Get(nextOperation.OperationId)!.Operation;
            Check(cancelledOperation.State == InstallationOperationState.Cancelled, "cooperative domain cancellation records its own distinct terminal state");
            var cancelledRun = plans.RecordReconciliation(nextRun.RunId, "subject", new("runtime", cancelledOperation,
                runtime.Installation with { Receipt = new(nextOperation.OperationId, RuntimeSwitchOutcome.Cancelled, null) }), cancelling.Revision);
            Check(cancelledRun.State == TaskRunState.Cancelled, "matching cancellation receipt and restored state are required before the task becomes cancelled");
            var file = Path.Combine(root, "data", "installation-operations.json");
            var json = JsonNode.Parse(File.ReadAllText(file))!;
            json["entries"]![0]!.AsObject().Remove("taskOwner"); File.WriteAllText(file, json.ToJsonString());
            try { new InstallationOperationStore(host.Services.GetRequiredService<IHostEnvironment>()).Read(); throw new Exception("Read legacy task ownership format"); }
            catch (InstallationException ex) { Check(ex.ProblemCode == InstallationProblemCodes.StoreUnavailable && File.ReadAllText(file) == json.ToJsonString(),
                "missing ownership field fails closed and preserves the ledger instead of parsing a legacy format"); }
        }
        finally { Directory.Delete(root, true); }
        return count;
    }
    private sealed class Planning(RuntimeChangeFixture runtime) : IRuntimeTaskChangePlanningService
    {
        public Task<TaskPlan> PlanAsync(string subject, TaskPlanIntent intent, CancellationToken ct) =>
            new TaskPlanner(RuntimeChangePlanningActions.Create(runtime)).PlanAsync(new("host", subject), intent, ct);
    }
    private sealed class Policies(TaskExecutionIdentity live) : ITaskApprovalPolicyReader, ITaskExecutionPolicyReader
    {
        public bool Denied;
        public TaskExecutionPolicyObservation Read(ClaimsPrincipal principal, string boundHostId)
        { if (Denied) throw new TaskApprovalException("task.permission_denied"); return new(live, DateTimeOffset.UtcNow.AddMinutes(1)); }
        public Task<TaskApprovalObservation> ReadAsync(ClaimsPrincipal principal, CancellationToken ct)
        {
            var local = Read(principal, "host");
            return Task.FromResult(new TaskApprovalObservation(live, new("1000", "alice", HostPlatformKind.Linux, "Alice", null),
                HostAccountPrivilege.StandardUser, local.ValidUntil, new RuntimeChangeFixture().Installation));
        }
    }
    private sealed class Approvers : ITaskApproverPolicyReader { public void Validate(string canonicalIdentity) { } }
    private sealed class Domain : IInstallationService
    {
        public int Starts, Recoveries; public TaskStepExecutionAuthorization? Authority;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationReceived; public Action? DuringCancel;
        public Action? DuringValidate;
        public InstallationServiceId Id => InstallationServiceId.SingBox;
        public IReadOnlyList<string> Resources => SingBoxInstallationService.ResourceKeys;
        public object Validate(InstallationOperationKind kind, JsonElement options, string idempotencyKey)
        {
            var action = DuringValidate; DuringValidate = null; action?.Invoke();
            return new SingBoxInstallationService(null!).Validate(kind, options, idempotencyKey);
        }
        public async Task StartAsync(InstallationOperationKind kind, object options, string actor, IInstallationProgress progress, CancellationToken ct)
        {
            Authority = ((ITaskInstallationProgress)progress).Authority;
            await Authority.ValidateAsync(ct); Starts++; Entered.TrySetResult(); await Release.Task.WaitAsync(ct);
            if (CancellationReceived) throw new InstallationCancelledException("runtime.operation_cancelled");
        }
        public Task<InstallationRecovery> RecoverAsync(InstallationOperationDto operation, CancellationToken ct)
        { Recoveries++; return Task.FromResult(new InstallationRecovery(InstallationOperationState.Interrupted)); }
        public bool Cancel(InstallationOperationDto operation) => InstallationOperationStore.Active(operation);
        public bool CooperativeCancel => true;
        public Task RequestCancelAsync(InstallationOperationDto operation, CancellationToken ct)
        { DuringCancel?.Invoke(); CancellationReceived = true; Release.TrySetResult(); return Task.CompletedTask; }
    }
}
