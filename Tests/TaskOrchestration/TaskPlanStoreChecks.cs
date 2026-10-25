using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.TaskOrchestration;

internal static class TaskPlanStoreChecks
{
    public static async Task<int> RunAsync(TaskPlan plan)
    {
        var count = 0;
        void Check(bool condition, string label)
        { if (!condition) throw new Exception(label); count++; Console.WriteLine("PASS: Plan store " + label); }
        void Reject(Action action, string code)
        {
            try { action(); throw new Exception("Accepted invalid store operation"); }
            catch (TaskPlanStoreException ex) { Check(ex.ProblemCode == code, "rejected " + code); }
        }
        var root = Path.Combine(Path.GetTempPath(), "task-plan-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "plans.db");
            var protection = new EphemeralDataProtectionProvider();
            var clock = new Clock(plan.CreatedAt.AddSeconds(1));
            var store = new TaskPlanStore(path, protection, clock);
            var saved = store.Save(plan, "subject");
            Check(saved is { Revision: 1, Validity: TaskPlanValidity.Resolved } && saved.Plan.PlanDigest == plan.PlanDigest,
                "freezes the plan and separate initial validity revision");
            var reopened = new TaskPlanStore(path, protection, clock);
            Check(reopened.Get(plan.PlanId, "subject")?.Plan.PlanDigest == plan.PlanDigest
                && reopened.Get(plan.PlanId, "subject")?.Plan.CreatedAt == plan.CreatedAt,
                "reopening retains the exact immutable envelope");
            Check(reopened.Get(plan.PlanId, "another") is null, "another subject cannot discover the plan");
            Reject(() => reopened.Invalidate(plan.PlanId, "another", plan.PlanDigest, 1), "task.plan_not_found");
            Reject(() => reopened.Save(plan, "another"), "task.plan_not_found");
            Check(store.Save(plan, "subject").Revision == 1, "repeated save is idempotent");
            var changed = plan.Semantics with { SourceIdentity = "different" };
            Reject(() => store.Save(plan with { Semantics = changed, PlanDigest = TaskPlanCanonicalizer.Digest(changed) }, "subject"),
                "task.plan_identity_conflict");
            Reject(() => store.Save(plan with { CreatedAt = plan.CreatedAt.AddSeconds(-1) }, "subject"), "task.plan_identity_conflict");
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                new TaskPlanStore(path, protection, clock).Save(plan, "subject"))));
            Check(concurrent.All(record => record.Plan.PlanId == plan.PlanId && record.Revision == 1),
                "independent concurrent store instances converge on one immutable plan");
            Reject(() => store.Invalidate(plan.PlanId, "subject", new string('0', 64), 1), "task.plan_digest_conflict");
            Reject(() => store.Invalidate(plan.PlanId, "subject", plan.PlanDigest, 9), "task.plan_revision_conflict");
            var invalidated = store.Invalidate(plan.PlanId, "subject", plan.PlanDigest, 1);
            Check(invalidated is { Validity: TaskPlanValidity.Invalidated, Revision: 2, ProblemCode: "task.plan_invalidated" }
                && invalidated.Plan.PlanDigest == plan.PlanDigest && invalidated.InvalidatedAt == clock.GetUtcNow(),
                "invalidation advances only validity and preserves immutable content");
            Check(new TaskPlanStore(path, protection, clock).Get(plan.PlanId, "subject")?.Validity == TaskPlanValidity.Invalidated,
                "invalidation and its audit survive reopening");
            Reject(() => store.Invalidate(plan.PlanId, "subject", plan.PlanDigest, 1), "task.plan_revision_conflict");
            Check(store.Invalidate(plan.PlanId, "subject", plan.PlanDigest, 2).Revision == 2,
                "repeated current-revision invalidation adds no duplicate audit event");
            var expiryPlan = plan with { PlanId = Guid.NewGuid() };
            store.Save(expiryPlan, "subject");
            var retryExpiryPlan = plan with { PlanId = Guid.NewGuid() };
            store.Save(retryExpiryPlan, "subject");
            clock.Now = saved.ExpiresAt;
            var expired = reopened.Get(expiryPlan.PlanId, "subject");
            Check(expired is { Validity: TaskPlanValidity.Invalidated, ProblemCode: "task.plan_expired", Revision: 2 },
                "expiry durably invalidates a plan rather than silently renewing its authority");
            Check(store.Save(expiryPlan, "subject").Validity == TaskPlanValidity.Invalidated,
                "saving an old envelope cannot resurrect its invalidated plan");
            Check(store.Save(retryExpiryPlan, "subject") is { Validity: TaskPlanValidity.Invalidated, ProblemCode: "task.plan_expired" },
                "retrying save after expiry records invalidity even without a prior query");
            var missingKeys = new TaskPlanStore(path, new EphemeralDataProtectionProvider(), clock);
            Reject(() => missingKeys.Get(plan.PlanId, "subject"), "task.plan_store_unavailable");
            clock.Now = plan.CreatedAt.AddSeconds(1);
            var quotaPath = Path.Combine(root, "quota.db");
            var quota = new TaskPlanStore(quotaPath, protection, clock, new(2, 1));
            quota.Save(plan, "subject");
            Reject(() => quota.Save(plan with { PlanId = Guid.NewGuid() }, "subject"), "task.plan_capacity_exceeded");
            Check(quota.Get(plan.PlanId, "subject")?.Validity == TaskPlanValidity.Resolved,
                "quota refusal preserves existing authoritative records");
            var atomicPath = Path.Combine(root, "atomic.db");
            var atomic = new TaskPlanStore(atomicPath, protection, clock, beforeCommit: point =>
            { if (point == TaskPlanCommitPoint.PlanWritten) throw new IOException("injected failure before commit"); });
            Reject(() => atomic.Save(plan, "subject"), "task.plan_store_unavailable");
            Check(new TaskPlanStore(atomicPath, protection, clock).Get(plan.PlanId, "subject") is null,
                "failed pre-commit insert leaves neither a partial plan nor audit after reopen");
            var atomicValidity = new TaskPlanStore(atomicPath, protection, clock, beforeCommit: point =>
            { if (point == TaskPlanCommitPoint.ValidityWritten) throw new IOException("injected failure before commit"); });
            atomicValidity.Save(plan, "subject");
            Reject(() => atomicValidity.Invalidate(plan.PlanId, "subject", plan.PlanDigest, 1), "task.plan_store_unavailable");
            Check(new TaskPlanStore(atomicPath, protection, clock).Get(plan.PlanId, "subject") is
                { Revision: 1, Validity: TaskPlanValidity.Resolved, ProblemCode: null },
                "failed pre-commit invalidation preserves the prior validity and audit atomically");
            using (var connection = Open(path))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE task_plans SET protected_record=zeroblob(64) WHERE plan_id=$id;";
                command.Parameters.AddWithValue("$id", plan.PlanId.ToString("D")); command.ExecuteNonQuery();
            }
            Reject(() => store.Get(plan.PlanId, "subject"), "task.plan_store_unavailable");
            Reject(() => store.Save(plan with { PlanId = Guid.NewGuid() }, "subject"), "task.plan_store_unavailable");
            Reject(() => new TaskPlanStore(path, protection, clock).Get(expiryPlan.PlanId, "subject"), "task.plan_store_unavailable");
            using (var connection = Open(quotaPath))
            using (var command = connection.CreateCommand())
            { command.CommandText = "DROP TABLE task_plans;"; command.ExecuteNonQuery(); }
            Reject(() => new TaskPlanStore(quotaPath, protection, clock).Save(plan, "subject"), "task.plan_store_unavailable");
            using (var connection = Open(quotaPath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='task_plans';";
                Check((long)command.ExecuteScalar()! == 0, "missing existing schema is preserved for repair instead of reset to empty");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        return count;
    }
    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open(); return connection;
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
