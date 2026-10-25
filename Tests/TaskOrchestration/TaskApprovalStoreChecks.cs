using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.TaskOrchestration;

internal static class TaskApprovalStoreChecks
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; Console.WriteLine("PASS: Approval/run " + label); }
        void Reject(Action action, string code)
        {
            try { action(); throw new Exception("Unexpected authority accepted"); }
            catch (TaskPlanStoreException ex) { Check(ex.ProblemCode == code, "rejects " + code); }
        }
        var root = Path.Combine(Path.GetTempPath(), "task-approval-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var plan = await new TaskPlanner(RuntimeChangePlanningActions.Create(new RuntimeChangeFixture()))
                .PlanAsync(new("host", "subject"), RuntimeChangePlanningChecks.Intent(RuntimeChange.Update));
            var path = Path.Combine(root, "plans.db");
            var protection = new EphemeralDataProtectionProvider();
            var clock = new Clock(plan.CreatedAt.AddSeconds(1));
            var store = new TaskPlanStore(path, protection, clock);
            store.Save(plan, "subject");
            var identity = new TaskApprovalIdentity("subject", "session-one", "host", "policy-one", "verified-admin:0", "fixture-authenticator", clock.GetUtcNow().AddMinutes(10));
            var live = new TaskExecutionIdentity("subject", "session-one", "host", "policy-one");
            var approval = store.RecordApproval(plan.PlanId, plan.PlanDigest, identity, "approval-key");
            Check(approval is { State: TaskApprovalState.Granted, Revision: 1, RunId: null }
                && approval.Capabilities is [{ Capability: "SingBoxInstall", Target: "singbox" }]
                && approval.StartBefore == clock.GetUtcNow().AddMinutes(2)
                && approval.RunAuthorizationExpiresAt == clock.GetUtcNow().AddMinutes(10),
                "separates exact scope, verified approver, start deadline and execution authorization deadline");
            Check(store.RecordApproval(plan.PlanId, plan.PlanDigest, identity, "approval-key").ApprovalId == approval.ApprovalId,
                "approval retries reuse the existing grant without extending deadlines");
            var shortPlan = plan with { PlanId = Guid.NewGuid() };
            store.Save(shortPlan, "subject");
            var shortIdentity = identity with { ValidUntil = clock.GetUtcNow().AddSeconds(30) };
            var shortGrant = store.RecordApproval(shortPlan.PlanId, shortPlan.PlanDigest, shortIdentity, "short-lived");
            Check(shortGrant.StartBefore == shortIdentity.ValidUntil && shortGrant.RunAuthorizationExpiresAt == shortIdentity.ValidUntil,
                "both deadlines are clipped to the live token/controller validity");
            var refreshed = store.RecordApproval(shortPlan.PlanId, shortPlan.PlanDigest, identity, "short-lived");
            Check(refreshed.ApprovalId == shortGrant.ApprovalId && refreshed.StartBefore == shortGrant.StartBefore
                && refreshed.RunAuthorizationExpiresAt == shortGrant.RunAuthorizationExpiresAt,
                "refreshed credentials cannot extend a previously issued approval");
            Reject(() => store.RecordApproval(plan.PlanId, plan.PlanDigest, identity with { ValidUntil = clock.GetUtcNow() }, "expired-evidence"),
                "task.approval_unavailable");
            Reject(() => store.RecordApproval(plan.PlanId, plan.PlanDigest, identity with { ApproverIdentity = "different" }, "approval-key"),
                "task.idempotency_conflict");
            Reject(() => store.RecordApproval(plan.PlanId, plan.PlanDigest, identity with { CanonicalSubject = "other" }, "another-grant"),
                "task.plan_not_found");
            Reject(() => store.StartRun(plan.PlanId, approval.ApprovalId, plan.PlanDigest, live with { CanonicalSubject = "other" }, "cross-subject"),
                "task.approval_not_found");
            Reject(() => store.RevokeApproval(approval.ApprovalId, "other"), "task.approval_not_found");
            foreach (var changed in new[] { live with { HostId = "other" }, live with { SessionIdentity = "other" }, live with { PolicyRevision = "other" } })
                Reject(() => store.StartRun(plan.PlanId, approval.ApprovalId, plan.PlanDigest, changed, Guid.NewGuid().ToString("N")),
                    "task.approval_identity_invalid");
            Reject(() => store.StartRun(plan.PlanId, approval.ApprovalId, new string('0', 64), live, "wrong-digest"), "task.approval_plan_mismatch");
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
                new TaskPlanStore(path, protection, clock).StartRun(plan.PlanId, approval.ApprovalId, plan.PlanDigest, live, "start-" + index))));
            var run = concurrent[0];
            Check(concurrent.Select(item => item.RunId).Distinct().Count() == 1 && run.State == TaskRunState.Queued
                && store.GetApproval(approval.ApprovalId, "subject") is { State: TaskApprovalState.Consumed, RunId: not null },
                "concurrent starts with different request keys consume one approval into one durable queued run");
            var reopened = new TaskPlanStore(path, protection, clock);
            Check(reopened.GetRun(run.RunId, "subject")?.Steps.Single().IdempotencyKey == run.Steps.Single().IdempotencyKey
                && run.Steps.Single() is { State: TaskStepRunState.Pending, RequestDigest: null, DomainOperationId: null },
                "reopening retains the same step dispatch key without pretending a domain operation was submitted");
            Check(reopened.StartRun(plan.PlanId, approval.ApprovalId, plan.PlanDigest, live, "start-1").RunId == run.RunId,
                "an aliased request key remains idempotent across reopening");
            Reject(() => reopened.StartRun(plan.PlanId, approval.ApprovalId, new string('1', 64), live, "start-1"), "task.idempotency_conflict");
            Check(reopened.GetApproval(approval.ApprovalId, "other") is null && reopened.GetRun(run.RunId, "other") is null,
                "approval and run identities do not disclose records to another actor");
            Reject(() => reopened.RecordApproval(plan.PlanId, plan.PlanDigest, identity, "duplicate-full-run"), "task.plan_already_started");
            var revoked = reopened.RevokeApproval(approval.ApprovalId, "subject");
            Check(revoked is { State: TaskApprovalState.Revoked, Revision: 3 } && revoked.RunId == run.RunId,
                "revocation preserves the consumed run binding and audit");
            Reject(() => reopened.StartRun(plan.PlanId, approval.ApprovalId, plan.PlanDigest, live, "after-revocation"), "task.approval_unavailable");
            Check(reopened.StartRun(plan.PlanId, approval.ApprovalId, plan.PlanDigest, live, "start-0").RunId == run.RunId,
                "a retry can retrieve a revoked run identity without creating new authority");
            var expiringPlan = plan with { PlanId = Guid.NewGuid() };
            reopened.Save(expiringPlan, "subject");
            var expiring = reopened.RecordApproval(expiringPlan.PlanId, expiringPlan.PlanDigest, identity, "expires");
            clock.Now = expiring.StartBefore;
            Check(reopened.GetApproval(expiring.ApprovalId, "subject") is { State: TaskApprovalState.Expired }, "unconsumed start authorization expires durably");
            Check(reopened.RecordApproval(expiringPlan.PlanId, expiringPlan.PlanDigest, identity, "expires").State == TaskApprovalState.Expired,
                "idempotent approval retry cannot renew expired start authorization");
            Reject(() => reopened.StartRun(expiringPlan.PlanId, expiring.ApprovalId, expiringPlan.PlanDigest, live, "expired-start"), "task.approval_unavailable");
            clock.Now = plan.CreatedAt.AddSeconds(1);
            var invalidPlan = plan with { PlanId = Guid.NewGuid() };
            reopened.Save(invalidPlan, "subject");
            var invalidApproval = reopened.RecordApproval(invalidPlan.PlanId, invalidPlan.PlanDigest, identity, "invalidated");
            reopened.Invalidate(invalidPlan.PlanId, "subject", invalidPlan.PlanDigest, 1);
            Check(reopened.GetApproval(invalidApproval.ApprovalId, "subject") is { State: TaskApprovalState.Revoked },
                "explicit plan invalidation atomically revokes related approvals");
            var blockedSemantics = plan.Semantics with { Steps = [plan.Semantics.Steps[0] with { Disposition = TaskStepDisposition.Conflict }] };
            var blocked = plan with { PlanId = Guid.NewGuid(), Semantics = blockedSemantics, PlanDigest = TaskPlanCanonicalizer.Digest(blockedSemantics) };
            reopened.Save(blocked, "subject");
            Reject(() => reopened.RecordApproval(blocked.PlanId, blocked.PlanDigest, identity, "blocked"), "task.plan_blocked");
            var oldSemantics = plan.Semantics with { Steps = [plan.Semantics.Steps[0] with { ImplementationRevision = "obsolete" }] };
            var old = plan with { PlanId = Guid.NewGuid(), Semantics = oldSemantics, PlanDigest = TaskPlanCanonicalizer.Digest(oldSemantics) };
            reopened.Save(old, "subject");
            Reject(() => reopened.RecordApproval(old.PlanId, old.PlanDigest, identity, "obsolete"), "task.action_revision_unsupported");
            var competingPlan = plan with { PlanId = Guid.NewGuid() };
            reopened.Save(competingPlan, "subject");
            var candidates = new[]
            {
                reopened.RecordApproval(competingPlan.PlanId, competingPlan.PlanDigest, identity, "candidate-1"),
                reopened.RecordApproval(competingPlan.PlanId, competingPlan.PlanDigest, identity, "candidate-2")
            };
            var competing = await Task.WhenAll(candidates.Select((candidate, index) => Task.Run(() =>
            {
                try { return (Run: reopened.StartRun(competingPlan.PlanId, candidate.ApprovalId, competingPlan.PlanDigest, live, "candidate-start-" + index), Code: ""); }
                catch (TaskPlanStoreException ex) { return (Run: (TaskRun?)null, Code: ex.ProblemCode); }
            })));
            Check(competing.Count(result => result.Run is not null) == 1
                && competing.Single(result => result.Run is null).Code == "task.plan_already_started",
                "competing approvals cannot allocate two full runs for one frozen plan");
            Check(competing.Single(result => result.Run is not null).Run!.Steps.Single().IdempotencyKey != run.Steps.Single().IdempotencyKey,
                "different run identities never share a domain dispatch key");

            foreach (var point in new[] { TaskPlanCommitPoint.ApprovalConsumed, TaskPlanCommitPoint.RunWritten })
            {
                var faultPath = Path.Combine(root, "atomic-" + point + ".db");
                var faultStore = new TaskPlanStore(faultPath, protection, clock, beforeCommit: current =>
                    { if (current == point) throw new IOException("injected commit boundary failure"); });
                faultStore.Save(plan, "subject");
                var faultApproval = faultStore.RecordApproval(plan.PlanId, plan.PlanDigest, identity, "grant");
                Reject(() => faultStore.StartRun(plan.PlanId, faultApproval.ApprovalId, plan.PlanDigest, live, "run"), "task.plan_store_unavailable");
                var after = new TaskPlanStore(faultPath, protection, clock);
                Check(after.GetApproval(faultApproval.ApprovalId, "subject") is { State: TaskApprovalState.Granted, RunId: null },
                    "commit failure rolls approval consumption and run allocation back together: " + point);
                var recovered = after.StartRun(plan.PlanId, faultApproval.ApprovalId, plan.PlanDigest, live, "run");
                Check(recovered.State == TaskRunState.Queued, "the original request key can safely allocate after rolled-back failure");
            }
            var grantPath = Path.Combine(root, "atomic-grant.db");
            var grantStore = new TaskPlanStore(grantPath, protection, clock, beforeCommit: point =>
                { if (point == TaskPlanCommitPoint.ApprovalWritten) throw new IOException("injected grant commit failure"); });
            grantStore.Save(plan, "subject");
            Reject(() => grantStore.RecordApproval(plan.PlanId, plan.PlanDigest, identity, "grant"), "task.plan_store_unavailable");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = grantPath, Pooling = false }.ToString()))
            {
                connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM task_approvals;";
                Check((long)command.ExecuteScalar()! == 0, "failed approval creation persists no partial grant or audit");
            }
            var revokePath = Path.Combine(root, "atomic-invalidation.db");
            var failRevoke = false;
            var revokeStore = new TaskPlanStore(revokePath, protection, clock, beforeCommit: point =>
            { if (failRevoke && point == TaskPlanCommitPoint.ApprovalWritten) throw new IOException("injected invalidation commit failure"); });
            revokeStore.Save(plan, "subject");
            var revokeApproval = revokeStore.RecordApproval(plan.PlanId, plan.PlanDigest, identity, "grant");
            failRevoke = true;
            Reject(() => revokeStore.Invalidate(plan.PlanId, "subject", plan.PlanDigest, 1), "task.plan_store_unavailable");
            var afterRevokeFailure = new TaskPlanStore(revokePath, protection, clock);
            Check(afterRevokeFailure.Get(plan.PlanId, "subject")?.Validity == TaskPlanValidity.Resolved
                && afterRevokeFailure.GetApproval(revokeApproval.ApprovalId, "subject")?.State == TaskApprovalState.Granted,
                "plan invalidation and approval revocation cannot partially commit");
            var oldSchemaPath = Path.Combine(root, "old-schema.db");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = oldSchemaPath, Pooling = false }.ToString()))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE task_plans(plan_id TEXT); PRAGMA user_version=1;"; command.ExecuteNonQuery();
            }
            Reject(() => new TaskPlanStore(oldSchemaPath, protection, clock).Save(plan, "subject"), "task.plan_store_unavailable");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = oldSchemaPath, Pooling = false }.ToString()))
            {
                connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version;";
                Check((long)command.ExecuteScalar()! == 1, "unsupported storage format is preserved, with no fallback parser, reset or migration adapter");
            }
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "UPDATE task_approvals SET protected_record=zeroblob(64) WHERE approval_id=$id;";
                command.Parameters.AddWithValue("$id", approval.ApprovalId.ToString("D")); command.ExecuteNonQuery();
            }
            Reject(() => new TaskPlanStore(path, protection, clock).GetRun(run.RunId, "subject"), "task.plan_store_unavailable");
        }
        finally { Directory.Delete(root, recursive: true); }
        return count;
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
