using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.TaskOrchestration;
using RelaxKonOS.Server.Installations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class TaskDispatchJournalChecks
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; }
        void Reject(Action action, string code)
        {
            try { action(); throw new Exception("Accepted invalid dispatch"); }
            catch (TaskPlanStoreException ex) { Check(ex.ProblemCode == code, code); }
        }
        var root = Path.Combine(Path.GetTempPath(), "task-dispatch-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var plan = await new TaskPlanner(RuntimeChangePlanningActions.Create(new RuntimeChangeFixture()))
                .PlanAsync(new("host", "subject"), RuntimeChangePlanningChecks.Intent(RuntimeChange.Update));
            var path = Path.Combine(root, "journal.db"); var protection = new EphemeralDataProtectionProvider();
            var store = new TaskPlanStore(path, protection);
            store.Save(plan, "subject");
            var identity = new TaskExecutionIdentity("subject", "session", "host", "policy");
            var approvalIdentity = new TaskApprovalIdentity("subject", "session", "host", "policy", "linux:0", "fixture", DateTimeOffset.UtcNow.AddMinutes(5));
            var approval = store.RecordApproval(plan.PlanId, plan.PlanDigest, approvalIdentity, "approve");
            var run = store.StartRun(plan.PlanId, approval.ApprovalId, plan.PlanDigest, identity, "start");
            Reject(() => store.PrepareDispatch(run.RunId, "runtime", identity with { SessionIdentity = "other" }, 1), "task.approval_identity_invalid");
            Reject(() => store.PrepareDispatch(run.RunId, "runtime", identity, 5), "task.run_revision_conflict");
            Reject(() => store.PrepareDispatch(run.RunId, "absent", identity, 1), "task.step_not_found");
            var prepared = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
                new TaskPlanStore(path, protection).PrepareDispatch(run.RunId, "runtime", identity, 1))));
            var intent = prepared[0];
            Check(prepared.All(item => item.RequestDigest == intent.RequestDigest)
                && store.GetRun(run.RunId, "subject") is { Revision: 2, State: TaskRunState.Running, Steps: [{ State: TaskStepRunState.Dispatching, DomainOperationId: null }] },
                "concurrent prepare commits exactly one intent before any domain acceptance");
            var request = TaskPlanJson.ReadInputs<ManagedRuntimeInstallationRequest>(intent.Request);
            Check(request.Confirmed && intent.Kind == InstallationOperationKind.Upgrade && intent.Service == InstallationServiceId.SingBox
                && request.Plan.Release?.ReleaseId == "target" && request.Plan.Request.IdempotencyKey == run.Steps.Single().IdempotencyKey
                && request.Plan.Request.IdempotencyKey != RuntimeChangePlanningActions.PlanningProbeKey,
                "exact frozen artifact and stable run/step key form the installation-domain request");
            using var host = Host.CreateDefaultBuilder().UseContentRoot(root).Build();
            var ledger = new InstallationOperationStore(host.Services.GetRequiredService<IHostEnvironment>());
            var coordinator = new InstallationCoordinator(ledger, [new SingBoxInstallationService(null!)],
                host.Services.GetRequiredService<IHostApplicationLifetime>());
            var key = request.Plan.Request.IdempotencyKey;
            Check(coordinator.FindByKey(intent.Service, intent.Kind, intent.Request, "subject", key) is null && ledger.Read().Length == 0,
                "lookup before acceptance never creates or executes an installation");
            var accepted = ledger.Create(intent.Service, intent.Kind, "subject", key,
                InstallationOperationStore.Reference(JsonSerializer.Serialize(request)), SingBoxInstallationService.ResourceKeys, out _);
            Check(coordinator.FindByKey(intent.Service, intent.Kind, intent.Request, "subject", key)?.Operation.OperationId == accepted.Operation.OperationId,
                "accepted operation is found by the exact typed request and original key without resubmission");
            Check(coordinator.FindByKey(intent.Service, intent.Kind, intent.Request, "other", key) is null,
                "another actor cannot find the operation by guessing its step key");
            try
            {
                coordinator.FindByKey(intent.Service, intent.Kind, TaskPlanJson.FreezeInputs(request with
                { Plan = request.Plan with { PreviousReleaseId = "changed" } }), "subject", key);
                throw new Exception("Accepted changed request");
            }
            catch (InstallationException ex) { Check(ex.ProblemCode == InstallationProblemCodes.IdempotencyConflict, "same key with changed frozen request conflicts on read-only lookup"); }
            Check(store.GetDispatch(run.RunId, "runtime", "other") is null, "journal is isolated by actor");
            var reopened = new TaskPlanStore(path, protection);
            Check(reopened.GetDispatch(run.RunId, "runtime", "subject")?.RequestDigest == intent.RequestDigest
                && reopened.PrepareDispatch(run.RunId, "runtime", identity, 1).RequestDigest == intent.RequestDigest,
                "restart preserves the original request digest and key rather than resolving another request");
            var operation = Guid.NewGuid();
            Reject(() => reopened.BindDomainOperation(run.RunId, "runtime", "subject", new string('0', 64), operation, 2), "task.dispatch_conflict");
            Reject(() => reopened.BindDomainOperation(run.RunId, "runtime", "subject", intent.RequestDigest, operation, 1), "task.run_revision_conflict");
            var bound = reopened.BindDomainOperation(run.RunId, "runtime", "subject", intent.RequestDigest, operation, 2);
            Check(bound is { Revision: 3, Steps: [{ State: TaskStepRunState.Running }] }
                && bound.Steps.Single().DomainOperationId == operation, "domain operation is bound only to the persisted exact request");
            Check(new TaskPlanStore(path, protection).BindDomainOperation(run.RunId, "runtime", "subject", intent.RequestDigest, operation, 2).Revision == 3,
                "binding acknowledgement retries cannot allocate another revision or operation");
            Reject(() => reopened.BindDomainOperation(run.RunId, "runtime", "subject", intent.RequestDigest, Guid.NewGuid(), 3), "task.domain_operation_conflict");
            reopened.RevokeApproval(approval.ApprovalId, "subject");
            Reject(() => reopened.PrepareDispatch(run.RunId, "runtime", identity, 3), "task.approval_unavailable");
            Check(reopened.GetDispatch(run.RunId, "runtime", "subject")?.RequestDigest == intent.RequestDigest,
                "revocation stops new authority while retaining evidence for read-only reconciliation");
            foreach (var point in new[] { TaskPlanCommitPoint.DispatchWritten, TaskPlanCommitPoint.OperationBound })
            {
                var next = plan with { PlanId = Guid.NewGuid() }; reopened.Save(next, "subject");
                var grant = reopened.RecordApproval(next.PlanId, next.PlanDigest, approvalIdentity, Guid.NewGuid().ToString("N"));
                var nextRun = reopened.StartRun(next.PlanId, grant.ApprovalId, next.PlanDigest, identity, Guid.NewGuid().ToString("N"));
                var faulting = new TaskPlanStore(path, protection, beforeCommit: at => { if (at == point) throw new IOException("injected"); });
                TaskDispatchIntent? nextIntent = null;
                if (point == TaskPlanCommitPoint.OperationBound) nextIntent = reopened.PrepareDispatch(nextRun.RunId, "runtime", identity, 1);
                Reject(() =>
                {
                    if (point == TaskPlanCommitPoint.DispatchWritten) faulting.PrepareDispatch(nextRun.RunId, "runtime", identity, 1);
                    else faulting.BindDomainOperation(nextRun.RunId, "runtime", "subject", nextIntent!.RequestDigest, Guid.NewGuid(), 2);
                }, "task.plan_store_unavailable");
                var intact = new TaskPlanStore(path, protection).GetRun(nextRun.RunId, "subject")!;
                Check(intact.Revision == (point == TaskPlanCommitPoint.DispatchWritten ? 1 : 2)
                    && intact.Steps.Single().DomainOperationId is null,
                    "commit fault rolls back step, audit and dispatch/operation binding together");
            }
            foreach (var tamper in new[] { "digest", "audit" })
            {
                var copied = Path.Combine(root, "tampered-" + tamper + ".db");
                File.Copy(path, copied);
                using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copied, Pooling = false }.ToString()))
                {
                    connection.Open(); using var query = connection.CreateCommand();
                    query.CommandText = "SELECT protected_record FROM task_runs WHERE run_id=$id;";
                    query.Parameters.AddWithValue("$id", run.RunId.ToString("D"));
                    var protector = protection.CreateProtector("RelaxKonOS.TaskOrchestration.Plans.v1");
                    var node = JsonNode.Parse(protector.Unprotect((byte[])query.ExecuteScalar()!))!;
                    if (tamper == "digest")
                    {
                        node["dispatches"]![0]!["requestDigest"] = new string('0', 64);
                        node["record"]!["steps"]![0]!["requestDigest"] = new string('0', 64);
                    }
                    else node["audit"]![1]!["event"] = "operation_bound";
                    using var update = connection.CreateCommand();
                    update.CommandText = "UPDATE task_runs SET protected_record=$body WHERE run_id=$id;";
                    update.Parameters.AddWithValue("$id", run.RunId.ToString("D"));
                    update.Parameters.AddWithValue("$body", protector.Protect(JsonSerializer.SerializeToUtf8Bytes(node)));
                    update.ExecuteNonQuery();
                }
                Reject(() => new TaskPlanStore(copied, protection).GetRun(run.RunId, "subject"), "task.plan_store_unavailable");
            }
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version=2;"; command.ExecuteNonQuery();
            }
            Reject(() => new TaskPlanStore(path, protection).GetRun(run.RunId, "subject"), "task.plan_store_unavailable");
        }
        finally { Directory.Delete(root, true); }
        return count;
    }
}
