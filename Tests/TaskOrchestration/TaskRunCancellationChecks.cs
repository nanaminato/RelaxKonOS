using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.TaskOrchestration;

internal static class TaskRunCancellationChecks
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; }
        void Reject(Action action, string code)
        {
            try { action(); throw new Exception("Unexpected cancellation authority"); }
            catch (TaskPlanStoreException ex) { Check(ex.ProblemCode == code, code); }
        }
        var root = Path.Combine(Path.GetTempPath(), "task-cancel-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var fixture = new RuntimeChangeFixture();
            var plan = await new TaskPlanner(RuntimeChangePlanningActions.Create(fixture)).PlanAsync(new("host", "subject"), RuntimeChangePlanningChecks.Intent(RuntimeChange.Update));
            var path = Path.Combine(root, "plans.db"); var protection = new EphemeralDataProtectionProvider(); var store = new TaskPlanStore(path, protection);
            var live = new TaskExecutionIdentity("subject", "session", "host", "policy");
            TaskRun Setup(int stage)
            {
                var next = plan with { PlanId = Guid.NewGuid() }; store.Save(next, "subject");
                var approval = store.RecordApproval(next.PlanId, next.PlanDigest, new("subject", "session", "host", "policy", "linux:0", "fixture", DateTimeOffset.UtcNow.AddMinutes(5)), Guid.NewGuid().ToString("N"));
                var run = store.StartRun(next.PlanId, approval.ApprovalId, next.PlanDigest, live, Guid.NewGuid().ToString("N"));
                if (stage == 0) return run;
                var dispatch = store.PrepareDispatch(run.RunId, "runtime", live, 1);
                return stage == 1 ? store.GetRun(run.RunId, "subject")! : store.BindDomainOperation(run.RunId, "runtime", "subject", dispatch.RequestDigest, Guid.NewGuid(), 2);
            }
            var queued = Setup(0);
            Reject(() => store.RequestRunCancellation(queued.RunId, "other", 1), "task.run_not_found");
            Reject(() => store.RequestRunCancellation(queued.RunId, "subject", 99), "task.run_revision_conflict");
            var cancelled = store.RequestRunCancellation(queued.RunId, "subject", 1);
            Check(cancelled is { State: TaskRunState.Cancelled, CancellationRequested: true, Steps: [{ State: TaskStepRunState.Skipped }] }, "unsubmitted pending work cancels without domain effects");
            Check(new TaskPlanStore(path, protection).GetRun(queued.RunId, "subject")?.State == TaskRunState.Cancelled
                && store.RequestRunCancellation(queued.RunId, "subject", 1).Revision == cancelled.Revision,
                "cancellation survives reopening and request retries do not add revisions");
            var uncertain = Setup(1); var requested = store.RequestRunCancellation(uncertain.RunId, "subject", 2);
            Check(requested.State == TaskRunState.Cancelling && requested.Steps[0].DomainOperationId is null,
                "an unresolved dispatch intent is not falsely declared cancelled");
            Reject(() => store.PrepareDispatch(uncertain.RunId, "runtime", live, requested.Revision), "task.step_not_ready");
            var existing = Setup(2);
            var policies = new Policies(live); var auth = new TaskStepAuthorizationService(store, policies, new Approvers());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "subject")], "fixture"));
            var step = existing.Steps[0];
            var bound = auth.Bind(principal, existing.RunId, step.StepId, step.DomainOperationId!.Value, step.RequestDigest!);
            requested = store.RequestRunCancellation(existing.RunId, "subject", 3);
            Check(requested.State == TaskRunState.Cancelling && await bound.ValidateAsync(default) > DateTimeOffset.UtcNow,
                "an already bound operation retains only its stage authority to converge at the host's safe boundary");
            try { auth.Bind(principal, existing.RunId, step.StepId, step.DomainOperationId!.Value, step.RequestDigest!); throw new Exception("Created new context after cancellation"); }
            catch (TaskApprovalException ex) { Check(ex.ProblemCode == "task.execution_authority_invalid", "cancellation forbids creating another execution context"); }
            var op = new InstallationOperationDto(step.DomainOperationId.Value, InstallationServiceId.SingBox, InstallationOperationKind.Upgrade,
                InstallationOperationState.Succeeded, InstallationStage.Completed, null, null, existing.CreatedAt, existing.CreatedAt, DateTimeOffset.UtcNow, false);
            var succeeded = store.RecordReconciliation(existing.RunId, "subject", new("runtime", op,
                fixture.Installation with { Installation = fixture.Installation.Installation with { StateRevision = 8, ActiveReleaseId = "target" },
                    MeasuredVersion = "1.14.3", Receipt = new(op.OperationId, RuntimeSwitchOutcome.Succeeded, null) }), requested.Revision);
            Check(succeeded.State == TaskRunState.Succeeded && succeeded.CancellationRequested,
                "a late cancellation cannot rewrite a successfully committed and observed operation as cancelled");
            Check(store.RequestRunCancellation(existing.RunId, "subject", 1).Revision == succeeded.Revision,
                "late terminal cancellation cannot alter completed history");
            var interrupted = Setup(0);
            var broken = new TaskPlanStore(path, protection, beforeCommit: point => { if (point == TaskPlanCommitPoint.CancellationWritten) throw new IOException("injected"); });
            Reject(() => broken.RequestRunCancellation(interrupted.RunId, "subject", 1), "task.plan_store_unavailable");
            Check(new TaskPlanStore(path, protection).GetRun(interrupted.RunId, "subject") is { Revision: 1, State: TaskRunState.Queued, CancellationRequested: false },
                "cancellation flag, skipped steps and audit roll back together on commit failure");
        }
        finally { Directory.Delete(root, true); }
        return count;
    }
    private sealed class Policies(TaskExecutionIdentity live) : ITaskExecutionPolicyReader
    { public TaskExecutionPolicyObservation Read(ClaimsPrincipal principal, string boundHostId) => new(live, DateTimeOffset.UtcNow.AddMinutes(1)); }
    private sealed class Approvers : ITaskApproverPolicyReader { public void Validate(string canonicalIdentity) { } }
}
