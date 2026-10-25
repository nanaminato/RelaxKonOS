using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.TaskOrchestration;

internal static class TaskUnacceptedCancellationChecks
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; }
        var root = Path.Combine(Path.GetTempPath(), "task-cancel-fence-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var host = Host.CreateDefaultBuilder().UseContentRoot(root).Build();
            var protection = new EphemeralDataProtectionProvider(); var path = Path.Combine(root, "plans.db"); var store = new TaskPlanStore(path, protection);
            var ledger = new InstallationOperationStore(host.Services.GetRequiredService<IHostEnvironment>());
            var coordinator = new InstallationCoordinator(ledger, [new SingBoxInstallationService(null!)], host.Services.GetRequiredService<IHostApplicationLifetime>());
            await coordinator.StartAsync(default);
            var domain = new SingBoxTaskReconciliationDomain(coordinator, null!);
            var live = new TaskExecutionIdentity("subject", "session", "host", "policy"); var policies = new Policies(live);
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "subject")], "fixture"));
            var plan = await new TaskPlanner(RuntimeChangePlanningActions.Create(new RuntimeChangeFixture()))
                .PlanAsync(new("host", "subject"), RuntimeChangePlanningChecks.Intent(RuntimeChange.Update));
            TaskRun Setup()
            {
                var next = plan with { PlanId = Guid.NewGuid() }; store.Save(next, "subject");
                var approval = store.RecordApproval(next.PlanId, next.PlanDigest, new("subject", "session", "host", "policy", "linux:0", "fixture",
                    DateTimeOffset.UtcNow.AddMinutes(5)), Guid.NewGuid().ToString("N"));
                var run = store.StartRun(next.PlanId, approval.ApprovalId, next.PlanDigest, live, Guid.NewGuid().ToString("N"));
                store.PrepareDispatch(run.RunId, "runtime", live, 1); return store.GetRun(run.RunId, "subject")!;
            }
            var service = new TaskRunCancellationService(store, policies, domain, coordinator);
            var first = Setup(); var closed = await service.RequestAsync(principal, first.RunId, 2, default);
            Check(closed is { State: TaskRunState.Cancelled, Revision: 4, CancellationRequested: true,
                Steps: [{ State: TaskStepRunState.Cancelled, DomainOperationId: null, ProblemCode: "task.cancelled_before_acceptance" }] }
                && ledger.Read().Length == 0, "coordinator fence proves no acceptance and closes only the original dispatch intent");
            Check(new TaskPlanStore(path, protection).GetRun(first.RunId, "subject")?.State == TaskRunState.Cancelled,
                "no-acceptance cancellation replays its distinct protected audit on reopening");
            Check((await service.RequestAsync(principal, first.RunId, 2, default)).Revision == 4, "fenced closure retry preserves the original revision");
            var fault = Setup();
            var broken = new TaskPlanStore(path, protection, beforeCommit: point => { if (point == TaskPlanCommitPoint.UnacceptedCancellationWritten) throw new IOException("injected"); });
            try { await new TaskRunCancellationService(broken, policies, domain, coordinator).RequestAsync(principal, fault.RunId, 2, default); throw new Exception("Committed close fault"); }
            catch (TaskPlanStoreException ex) { Check(ex.ProblemCode == "task.plan_store_unavailable", "fenced close fails closed on a commit fault"); }
            var reopened = new TaskPlanStore(path, protection);
            Check(reopened.GetRun(fault.RunId, "subject") is { State: TaskRunState.Cancelling, Revision: 3, Steps: [{ State: TaskStepRunState.Dispatching }] },
                "close fault preserves the durable cancel request and rolls back only closure state/evidence");
            Check((await new TaskRunCancellationService(reopened, policies, domain, coordinator).RequestAsync(principal, fault.RunId, 2, default)).State == TaskRunState.Cancelled,
                "retry after reopening safely completes the original closure without any installation");
            var raced = Setup(); var dispatch = store.GetDispatch(raced.RunId, "runtime", "subject")!;
            var request = TaskPlanJson.ReadInputs<ManagedRuntimeInstallationRequest>(dispatch.Request);
            var accepted = ledger.Create(dispatch.Service, dispatch.Kind, "subject", request.Plan.Request.IdempotencyKey,
                InstallationOperationStore.Reference(JsonSerializer.Serialize(request)), SingBoxInstallationService.ResourceKeys, out _,
                new(raced.RunId, "runtime", dispatch.RequestDigest));
            ledger.Update(accepted.Operation.OperationId, value => value with { Cancellable = false }, "fixture-commit");
            var staleLookup = new InitiallyMissing(domain);
            var cancelling = await new TaskRunCancellationService(store, policies, staleLookup, coordinator).RequestAsync(principal, raced.RunId, 2, default);
            Check(cancelling is { State: TaskRunState.Cancelling, Steps: [{ State: TaskStepRunState.Running }] }
                && cancelling.Steps[0].DomainOperationId == accepted.Operation.OperationId && staleLookup.Calls == 2,
                "stale initial absence cannot close an operation already present under the acceptance fence");
            Check(new TaskPlanStore(path, protection).GetRun(raced.RunId, "subject")?.State == TaskRunState.Cancelling,
                "accepted-race cancellation retains operation identity for receipt reconciliation after restart");
            await coordinator.StopAsync(default);
        }
        finally { Directory.Delete(root, true); }
        return count;
    }
    private sealed class Policies(TaskExecutionIdentity identity) : ITaskExecutionPolicyReader
    { public TaskExecutionPolicyObservation Read(ClaimsPrincipal principal, string boundHostId) => new(identity, DateTimeOffset.UtcNow.AddMinutes(1)); }
    private sealed class InitiallyMissing(ITaskReconciliationDomain domain) : ITaskReconciliationDomain
    {
        public int Calls;
        public InstallationOperationDto? Find(Guid runId, TaskDispatchIntent dispatch, string subject) => ++Calls == 1 ? null : domain.Find(runId, dispatch, subject);
        public Task<RuntimeInstallationSnapshot> ObserveAsync(Guid operationId, CancellationToken ct) => throw new NotSupportedException();
    }
}
