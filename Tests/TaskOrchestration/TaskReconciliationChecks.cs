using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.Server.TaskOrchestration;
using RelaxKonOS.Server.ManagedRuntimes;

internal static class TaskReconciliationChecks
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        void Reject(Action action, string code)
        {
            try { action(); throw new Exception("Accepted contradictory result"); }
            catch (TaskPlanStoreException ex) { Check(ex.ProblemCode == code, code); }
        }
        var root = Path.Combine(Path.GetTempPath(), "task-result-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = new RuntimeChangeFixture();
            var plan = await new TaskPlanner(RuntimeChangePlanningActions.Create(fixture))
                .PlanAsync(new("host", "subject"), RuntimeChangePlanningChecks.Intent(RuntimeChange.Update));
            var path = Path.Combine(root, "results.db"); var protection = new EphemeralDataProtectionProvider();
            var store = new TaskPlanStore(path, protection);
            var live = new TaskExecutionIdentity("subject", "session", "host", "policy");
            (TaskRun Run, TaskPlanApproval Approval, TaskReconciliationEvidence Evidence) Setup()
            {
                var next = plan with { PlanId = Guid.NewGuid() }; store.Save(next, "subject");
                var approval = store.RecordApproval(next.PlanId, next.PlanDigest, new("subject", "session", "host", "policy",
                    "linux:0", "fixture", DateTimeOffset.UtcNow.AddMinutes(5)), Guid.NewGuid().ToString("N"));
                var run = store.StartRun(next.PlanId, approval.ApprovalId, next.PlanDigest, live, Guid.NewGuid().ToString("N"));
                var dispatch = store.PrepareDispatch(run.RunId, "runtime", live, 1); var operationId = Guid.NewGuid();
                run = store.BindDomainOperation(run.RunId, "runtime", "subject", dispatch.RequestDigest, operationId, 2);
                var operation = new InstallationOperationDto(operationId, InstallationServiceId.SingBox, InstallationOperationKind.Upgrade,
                    InstallationOperationState.Succeeded, InstallationStage.Completed, null, null, run.CreatedAt, run.CreatedAt, DateTimeOffset.UtcNow, false);
                var snapshot = fixture.Installation with { Installation = fixture.Installation.Installation with { StateRevision = 8, ActiveReleaseId = "target" },
                    MeasuredVersion = "1.14.3", Receipt = new(operationId, RuntimeSwitchOutcome.Succeeded, null) };
                return (run, approval, new("runtime", operation, snapshot));
            }
            var good = Setup();
            Reject(() => store.RecordReconciliation(good.Run.RunId, "subject", good.Evidence with
                { Operation = good.Evidence.Operation with { Service = InstallationServiceId.Mihomo } }, 3), "task.domain_operation_conflict");
            Reject(() => store.RecordReconciliation(good.Run.RunId, "subject", good.Evidence, 2), "task.run_revision_conflict");
            var active = good.Evidence with { Operation = good.Evidence.Operation with { State = InstallationOperationState.Running, CompletedAt = null } };
            Check(store.RecordReconciliation(good.Run.RunId, "subject", active, 3).Revision == 3, "active domain polling does not invent a terminal result or write redundant revisions");
            store.RevokeApproval(good.Approval.ApprovalId, "subject");
            var succeeded = store.RecordReconciliation(good.Run.RunId, "subject", good.Evidence, 3);
            Check(succeeded is { State: TaskRunState.Succeeded, Revision: 4, Steps: [{ State: TaskStepRunState.Succeeded }] },
                "matching domain terminal state, receipt and actual target prove success even after authority was revoked");
            Check(new TaskPlanStore(path, protection).GetRun(good.Run.RunId, "subject")?.State == TaskRunState.Succeeded,
                "reopening replays and validates persisted terminal evidence");
            Check(store.RecordReconciliation(good.Run.RunId, "subject", good.Evidence, 3).Revision == 4, "terminal acknowledgement retry does not change history");
            Reject(() => store.RecordReconciliation(good.Run.RunId, "subject", good.Evidence with { Snapshot = null }, 4), "task.result_conflict");
            var transforms = new Func<TaskReconciliationEvidence, TaskReconciliationEvidence>[]
            {
                e => e with { Snapshot = null },
                e => e with { Snapshot = e.Snapshot! with { Receipt = null } },
                e => e with { Snapshot = e.Snapshot! with { Receipt = e.Snapshot.Receipt! with { OperationId = Guid.NewGuid() } } },
                e => e with { Snapshot = e.Snapshot! with { Installation = e.Snapshot.Installation with { ActiveReleaseId = "other" } } },
                e => e with { Snapshot = e.Snapshot! with { Installation = e.Snapshot.Installation with { StateRevision = 7 } } },
                e => e with { Snapshot = e.Snapshot! with { Installation = e.Snapshot.Installation with { RecoveryRequired = true } } },
                e => e with { Snapshot = e.Snapshot! with { PendingOperationId = Guid.NewGuid() } },
                e => e with { Snapshot = e.Snapshot! with { ProblemCode = "runtime.configuration_drift" } },
                e => e with { Operation = e.Operation with { State = InstallationOperationState.Interrupted } }
            };
            foreach (var transform in transforms)
            {
                var current = Setup();
                var uncertain = store.RecordReconciliation(current.Run.RunId, "subject", transform(current.Evidence), 3);
                Check(uncertain is { State: TaskRunState.NeedsRecovery, Steps: [{ State: TaskStepRunState.NeedsRecovery }] }
                    && new TaskPlanStore(path, protection).GetRun(current.Run.RunId, "subject")?.ProblemCode == "task.domain_recovery_required",
                    "missing, conflicting or interrupted evidence stays NeedsRecovery across reopening");
            }
            foreach (var cancelled in new[] { false, true })
            {
                var current = Setup();
                var evidence = current.Evidence with
                {
                    Operation = current.Evidence.Operation with { State = cancelled ? InstallationOperationState.Cancelled : InstallationOperationState.Failed },
                    Snapshot = fixture.Installation with { Receipt = new(current.Evidence.Operation.OperationId,
                        cancelled ? RuntimeSwitchOutcome.Cancelled : RuntimeSwitchOutcome.Failed, null) }
                };
                Check(store.RecordReconciliation(current.Run.RunId, "subject", evidence, 3).State == (cancelled ? TaskRunState.Cancelled : TaskRunState.Failed),
                    "failed/cancelled is conclusive only with its own receipt and restored baseline");
            }
            var fault = Setup();
            var broken = new TaskPlanStore(path, protection, beforeCommit: point => { if (point == TaskPlanCommitPoint.ResultWritten) throw new IOException("injected"); });
            Reject(() => broken.RecordReconciliation(fault.Run.RunId, "subject", fault.Evidence, 3), "task.plan_store_unavailable");
            Check(new TaskPlanStore(path, protection).GetRun(fault.Run.RunId, "subject") is { Revision: 3, State: TaskRunState.Running },
                "result/audit/run state all roll back when commit fails");
            var observing = Setup();
            var domain = new Domain { Evidence = observing.Evidence };
            var service = new TaskRunReconciliationService(store, domain);
            domain.Missing = true;
            Check((await service.ReconcileAsync(observing.Run.RunId, "runtime", "subject", default)).Revision == 3
                && domain.Queries == 0, "missing domain acceptance never causes query, resubmission or terminal success");
            domain.Missing = false;
            Check((await service.ReconcileAsync(observing.Run.RunId, "runtime", "subject", default)).State == TaskRunState.Succeeded
                && domain.ObservedId == observing.Evidence.Operation.OperationId, "read-only coordinator queries the exact accepted operation receipt");
            var queries = domain.Queries;
            await service.ReconcileAsync(observing.Run.RunId, "runtime", "subject", default);
            Check(domain.Queries == queries, "terminal history is not overwritten by later polling");
            var unavailable = Setup(); domain.Evidence = unavailable.Evidence; domain.Unavailable = true;
            Check((await service.ReconcileAsync(unavailable.Run.RunId, "runtime", "subject", default)).State == TaskRunState.NeedsRecovery,
                "unavailable host evidence cannot be interpreted as domain success");
        }
        finally { Directory.Delete(root, true); }
        return count;
    }
    private sealed class Domain : ITaskReconciliationDomain
    {
        public TaskReconciliationEvidence Evidence = null!;
        public bool Missing, Unavailable;
        public int Queries; public Guid ObservedId;
        public InstallationOperationDto? Find(Guid runId, TaskDispatchIntent dispatch, string subject) => Missing ? null : Evidence.Operation;
        public Task<RuntimeInstallationSnapshot> ObserveAsync(Guid operationId, CancellationToken ct)
        {
            Queries++; ObservedId = operationId;
            return Unavailable ? Task.FromException<RuntimeInstallationSnapshot>(new RuntimeLifecycleException("runtime.helper_unavailable"))
                : Task.FromResult(Evidence.Snapshot!);
        }
    }
}
