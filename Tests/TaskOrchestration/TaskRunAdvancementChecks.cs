using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.TaskOrchestration;

internal static class TaskRunAdvancementChecks
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; }
        void Reject(Action action, string code)
        {
            try { action(); throw new Exception("Unexpected step progression"); }
            catch (TaskPlanStoreException ex) { Check(ex.ProblemCode == code, code); }
        }
        var root = Path.Combine(Path.GetTempPath(), "task-advance-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var fixture = new RuntimeChangeFixture(); var observations = new Observations(fixture);
            var original = RuntimeChangePlanningChecks.Intent(RuntimeChange.Update);
            var intent = original with { Steps = [new("check", "runtime.verifyInstallation", 1, "sing-box-read-1",
                TaskPlanJson.FreezeInputs(new VerifyRuntimeInstallationInput(RuntimeChangeFixture.InstallationId, 7)), []),
                original.Steps[0] with { DependsOn = ["check"] }] };
            var plan = await new TaskPlanner(RuntimeReadPlanningActions.Create(observations).Concat(RuntimeChangePlanningActions.Create(fixture)))
                .PlanAsync(new("host", "subject"), intent);
            var path = Path.Combine(root, "plans.db"); var protection = new EphemeralDataProtectionProvider();
            var store = new TaskPlanStore(path, protection);
            var live = new TaskExecutionIdentity("subject", "session", "host", "policy");
            TaskRun Setup()
            {
                var next = plan with { PlanId = Guid.NewGuid() }; store.Save(next, "subject");
                var approval = store.RecordApproval(next.PlanId, next.PlanDigest, new("subject", "session", "host", "policy",
                    "linux:0", "fixture", DateTimeOffset.UtcNow.AddMinutes(5)), Guid.NewGuid().ToString("N"));
                return store.StartRun(next.PlanId, approval.ApprovalId, next.PlanDigest, live, Guid.NewGuid().ToString("N"));
            }
            var policies = new Policies(live); var preview = new Preview(observations);
            var dispatch = new Dispatch(store, live); var reconcile = new Reconcile(store, fixture);
            var service = new TaskRunAdvancementService(store, policies, preview, dispatch, reconcile);
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "subject")], "fixture"));
            var run = Setup();
            Reject(() => store.PrepareDispatch(run.RunId, "runtime", live, 1), "task.step_not_ready");
            var checkedRun = await service.AdvanceAsync(principal, run.RunId, 1, default);
            Check(checkedRun is { Revision: 2, Steps: [{ State: TaskStepRunState.NoOp }, { State: TaskStepRunState.Pending }] }
                && dispatch.Calls == 0 && preview.Calls == 1, "first advancement records only the fresh read check and preserves the pending change");
            Check(new TaskPlanStore(path, protection).GetRun(run.RunId, "subject")?.Steps[0].State == TaskStepRunState.NoOp,
                "read evidence and NoOp survive reopening");
            var active = await service.AdvanceAsync(principal, run.RunId, 2, default);
            Check(active.Steps[1].State == TaskStepRunState.Running && dispatch.Calls == 1,
                "only a later advancement dispatches the dependent mutation");
            var complete = await service.AdvanceAsync(principal, run.RunId, 4, default);
            Check(complete.State == TaskRunState.Succeeded && reconcile.Calls == 1
                && new TaskPlanStore(path, protection).GetRun(run.RunId, "subject")?.State == TaskRunState.Succeeded,
                "NoOp plus verified mutation result converges to durable success");
            await service.AdvanceAsync(principal, run.RunId, complete.Revision, default);
            Check(dispatch.Calls == 1 && reconcile.Calls == 1 && preview.Calls == 1, "reading terminal progression does not repeat observations or effects");
            Check(store.RecordReadStep(run.RunId, live, new("check", preview.Last!), 1).Revision == complete.Revision,
                "the original read acknowledgement remains idempotent after later steps completed");
            var wrong = preview.Last!.Semantics with { InputRevision = preview.Last.Semantics.InputRevision + 1 };
            Reject(() => store.RecordReadStep(run.RunId, live, new("check", preview.Last with
                { Semantics = wrong, PlanDigest = TaskPlanCanonicalizer.Digest(wrong) }), complete.Revision), "task.observation_invalid");
            try
            {
                await service.AdvanceAsync(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "subject")])), run.RunId, complete.Revision, default);
                throw new Exception("Accepted unauthenticated advancement");
            }
            catch (TaskApprovalException ex) { Check(ex.ProblemCode == "task.session_unavailable", "unauthenticated subject claims cannot advance or inspect terminal progression"); }
            var blocked = Setup(); observations.Changed = true;
            var failed = await service.AdvanceAsync(principal, blocked.RunId, 1, default);
            Check(failed is { State: TaskRunState.Failed, ProblemCode: "task.read_check_failed",
                Steps: [{ State: TaskStepRunState.Failed }, { State: TaskStepRunState.Skipped }] } && dispatch.Calls == 1,
                "changed read baseline durably fails and skips the mutation without dispatch");
            Check(new TaskPlanStore(path, protection).GetRun(blocked.RunId, "subject")?.State == TaskRunState.Failed,
                "read failure audit replays across reopening");
            observations.Changed = false;
            var interrupted = Setup();
            var faulting = new TaskPlanStore(path, protection, beforeCommit: point => { if (point == TaskPlanCommitPoint.ReadResultWritten) throw new IOException("injected"); });
            var faultService = new TaskRunAdvancementService(faulting, policies, preview, dispatch, reconcile);
            try { await faultService.AdvanceAsync(principal, interrupted.RunId, 1, default); throw new Exception("Committed injected read fault"); }
            catch (TaskPlanStoreException ex) { Check(ex.ProblemCode == "task.plan_store_unavailable", "read witness transaction fails closed on injected commit fault"); }
            Check(new TaskPlanStore(path, protection).GetRun(interrupted.RunId, "subject") is { Revision: 1, State: TaskRunState.Queued },
                "read state, evidence and audit roll back together");
            var revoked = Setup(); observations.DuringRead = () => policies.Current = live with { PolicyRevision = "changed" };
            try { await service.AdvanceAsync(principal, revoked.RunId, 1, default); throw new Exception("Accepted changed live policy"); }
            catch (TaskApprovalException ex) { Check(ex.ProblemCode == "task.policy_changed" && store.GetRun(revoked.RunId, "subject")?.Revision == 1,
                "policy revocation during observation leaves no completed read step"); }
            foreach (var unsupported in new[] { 3, 4 })
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                connection.Open(); using var version = connection.CreateCommand(); version.CommandText = "PRAGMA user_version=" + unsupported; version.ExecuteNonQuery();
                Reject(() => new TaskPlanStore(path, protection).GetRun(run.RunId, "subject"), "task.plan_store_unavailable");
            }
        }
        finally { Directory.Delete(root, true); }
        return count;
    }
    private sealed class Policies(TaskExecutionIdentity identity) : ITaskExecutionPolicyReader
    {
        public TaskExecutionIdentity Current = identity;
        public TaskExecutionPolicyObservation Read(ClaimsPrincipal principal, string boundHostId) => new(Current, DateTimeOffset.UtcNow.AddMinutes(1));
    }
    private sealed class Observations(RuntimeChangeFixture fixture) : IRuntimeTaskObservations
    {
        public bool Changed; public Action? DuringRead;
        public Task<RuntimeInstallationSnapshot> InstallationAsync(CancellationToken ct)
        { DuringRead?.Invoke(); return Task.FromResult(Changed ? fixture.Installation with { Installation = fixture.Installation.Installation with { StateRevision = 8 } } : fixture.Installation); }
        public Task<RuntimeCatalogSnapshot> CatalogAsync(string sourceId, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Preview(Observations observations) : IRuntimeTaskPreviewService
    {
        public int Calls; public TaskPlan? Last;
        public async Task<TaskPlan> PreviewAsync(string subject, TaskPlanIntent intent, CancellationToken ct)
        { Calls++; return Last = await new TaskPlanner(RuntimeReadPlanningActions.Create(observations)).PlanAsync(new("host", subject), intent, ct); }
    }
    private sealed class Dispatch(TaskPlanStore store, TaskExecutionIdentity identity) : ITaskStepDispatcher
    {
        public int Calls;
        public Task<InstallationOperationDto> DispatchAsync(ClaimsPrincipal principal, Guid runId, string stepId, CancellationToken ct)
        {
            Calls++; var run = store.GetRun(runId, identity.CanonicalSubject)!;
            var intent = store.PrepareDispatch(runId, stepId, identity, run.Revision); var id = Guid.NewGuid();
            store.BindDomainOperation(runId, stepId, identity.CanonicalSubject, intent.RequestDigest, id, run.Revision + 1);
            return Task.FromResult(new InstallationOperationDto(id, InstallationServiceId.SingBox, InstallationOperationKind.Upgrade,
                InstallationOperationState.Running, InstallationStage.Preflight, null, null, run.CreatedAt, run.CreatedAt, null, false));
        }
    }
    private sealed class Reconcile(TaskPlanStore store, RuntimeChangeFixture fixture) : ITaskRunReconciler
    {
        public int Calls;
        public Task<TaskRun> ReconcileAsync(Guid runId, string stepId, string subject, CancellationToken ct)
        {
            Calls++; var run = store.GetRun(runId, subject)!; var id = run.Steps.Single(item => item.StepId == stepId).DomainOperationId!.Value;
            return Task.FromResult(store.RecordReconciliation(runId, subject, new(stepId,
                new(id, InstallationServiceId.SingBox, InstallationOperationKind.Upgrade, InstallationOperationState.Succeeded,
                    InstallationStage.Completed, null, null, run.CreatedAt, run.CreatedAt, DateTimeOffset.UtcNow, false),
                fixture.Installation with { Installation = fixture.Installation.Installation with { StateRevision = 8, ActiveReleaseId = "target" },
                    MeasuredVersion = "1.14.3", Receipt = new(id, RuntimeSwitchOutcome.Succeeded, null) }), run.Revision));
        }
    }
}
