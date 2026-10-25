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
            (TaskRun Run, TaskPlanApproval Approval, TaskReconciliationEvidence Evidence) Setup(TaskPlan? candidate = null)
            {
                var next = (candidate ?? plan) with { PlanId = Guid.NewGuid() }; store.Save(next, "subject");
                var approval = store.RecordApproval(next.PlanId, next.PlanDigest, new("subject", "session", "host", "policy",
                    "linux:0", "fixture", DateTimeOffset.UtcNow.AddMinutes(5)), Guid.NewGuid().ToString("N"));
                var run = store.StartRun(next.PlanId, approval.ApprovalId, next.PlanDigest, live, Guid.NewGuid().ToString("N"));
                var dispatch = store.PrepareDispatch(run.RunId, "runtime", live, 1); var operationId = Guid.NewGuid();
                run = store.BindDomainOperation(run.RunId, "runtime", "subject", dispatch.RequestDigest, operationId, 2);
                var operation = new InstallationOperationDto(operationId, dispatch.Service, dispatch.Kind,
                    InstallationOperationState.Succeeded, InstallationStage.Completed, null, null, run.CreatedAt, run.CreatedAt, DateTimeOffset.UtcNow, false);
                var snapshot = fixture.Installation with { Installation = fixture.Installation.Installation with { StateRevision = 8, ActiveReleaseId = "target" },
                    MeasuredVersion = "1.14.3", ActiveRevisionId = operationId, ConfigurationIdentity = new string('d', 64),
                    Receipt = new(operationId, RuntimeSwitchOutcome.Succeeded, null) };
                snapshot = snapshot with { CommitWitness = new(operationId, snapshot.Installation, snapshot.ActiveRevisionId,
                    snapshot.ConfigurationIdentity, snapshot.MeasuredVersion) };
                return (run, approval, new("runtime", operation, snapshot));
            }
            var good = Setup();
            var originalFrozen = TaskPlanJson.ReadInputs<RuntimeTaskFrozenChange>(plan.Semantics.Steps[0].Inputs);
            var tagSemantics = plan.Semantics with { Steps = [plan.Semantics.Steps[0] with
                { Inputs = TaskPlanJson.FreezeInputs(originalFrozen with { Release = originalFrozen.Release! with { OriginalVersion = "v1.14.3" } }) }] };
            var tagged = Setup(plan with { Semantics = tagSemantics, PlanDigest = TaskPlanCanonicalizer.Digest(tagSemantics) });
            Check(store.RecordReconciliation(tagged.Run.RunId, "subject", tagged.Evidence, 3).State == TaskRunState.Succeeded,
                "native canonical version matches a signed v-prefixed release tag without weakening release identity");
            var wrongBuild = Setup(plan with { Semantics = tagSemantics, PlanDigest = TaskPlanCanonicalizer.Digest(tagSemantics) });
            Check(store.RecordReconciliation(wrongBuild.Run.RunId, "subject", wrongBuild.Evidence with
                { Snapshot = wrongBuild.Evidence.Snapshot! with { MeasuredVersion = "1.14.3+other-build" } }, 3).State == TaskRunState.NeedsRecovery,
                "version reconciliation still requires exact canonical build identity, not just SemVer precedence");
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
            var historical = new TaskPlanStore(path, protection).GetRunEvidence(good.Run.RunId, "subject")!;
            Check(historical.Run.RunId == succeeded.RunId && historical.Run.Revision == succeeded.Revision
                && historical.Observations is [{ RecoveryCheck: false, Snapshot.MeasuredVersion: "1.14.3" }]
                && historical.Observations[0].OperationId == good.Evidence.Operation.OperationId,
                "historical result query preserves exact persisted host evidence across reopening");
            Check(store.GetRunEvidence(good.Run.RunId, "other") is null, "result evidence is isolated by task owner");
            var foreign = Setup();
            var foreignObservation = foreign.Evidence with { Snapshot = foreign.Evidence.Snapshot! with
                { Installation = foreign.Evidence.Snapshot.Installation with { HostId = "other-host" } } };
            store.RecordReconciliation(foreign.Run.RunId, "subject", foreignObservation, 3);
            Check(store.GetRunEvidence(foreign.Run.RunId, "subject") is { Run.State: TaskRunState.NeedsRecovery, Observations: [{ Snapshot: null }] },
                "evidence query withholds unrelated host observations without rewriting the uncertain result");
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

            // Explicit recovery checks append fresh evidence without altering the original result or granting execution.
            var recoverable = Setup();
            var uncertainReceipt = recoverable.Evidence with { Operation = recoverable.Evidence.Operation with { State = InstallationOperationState.Interrupted }, Snapshot = null };
            var needsRecovery = store.RecordReconciliation(recoverable.Run.RunId, "subject", uncertainReceipt, 3);
            domain = new Domain { Evidence = recoverable.Evidence with { Operation = uncertainReceipt.Operation } };
            var recoveryService = new TaskRecoveryReconciliationService(store, domain);
            await RejectRecovery("other", needsRecovery.Revision, "task.run_not_found");
            await RejectRecovery("subject", needsRecovery.Revision - 1, "task.run_revision_conflict");
            Check(domain.Queries == 0, "recovery identity and revision are checked before querying the host");
            domain.Missing = true;
            await RejectRecovery("subject", needsRecovery.Revision, "task.domain_operation_conflict");
            domain.Missing = false;
            var expectedEvidence = domain.Evidence;
            domain.Evidence = expectedEvidence with { Operation = expectedEvidence.Operation with { OperationId = Guid.NewGuid() } };
            await RejectRecovery("subject", needsRecovery.Revision, "task.domain_operation_conflict");
            Check(domain.Queries == 0, "recovery never queries a replacement operation");
            domain.Evidence = expectedEvidence with { Snapshot = expectedEvidence.Snapshot! with { PendingOperationId = Guid.NewGuid() } };
            needsRecovery = await recoveryService.RecheckAsync(needsRecovery.RunId, "subject", needsRecovery.Revision, default);
            Check(needsRecovery.State == TaskRunState.NeedsRecovery && needsRecovery.Revision == 5,
                "unresolved recovery evidence is retained with a new audit revision");
            Check((await recoveryService.RecheckAsync(needsRecovery.RunId, "subject", needsRecovery.Revision, default)).Revision == 5,
                "repeated identical recovery evidence does not inflate the journal");
            domain.Evidence = expectedEvidence; store.RevokeApproval(recoverable.Approval.ApprovalId, "subject");
            var recovered = await recoveryService.RecheckAsync(needsRecovery.RunId, "subject", needsRecovery.Revision, default);
            Check(recovered is { State: TaskRunState.Succeeded, Revision: 6 },
                "explicit check resolves interrupted ledger only with matching receipt and measured target, without renewing approval");
            Check(new TaskPlanStore(path, protection).GetRun(recovered.RunId, "subject") is { State: TaskRunState.Succeeded, Revision: 6 },
                "reopening replays original uncertain evidence and subsequent recovery evidence");
            var recoveryHistory = new TaskPlanStore(path, protection).GetRunEvidence(recovered.RunId, "subject")!;
            Check(recoveryHistory.Observations is [{ RecoveryCheck: false, Snapshot: null }, { RecoveryCheck: true }, { RecoveryCheck: true }]
                && recoveryHistory.Observations[^1].Snapshot?.Receipt?.Outcome == RuntimeSwitchOutcome.Succeeded,
                "evidence query preserves original uncertainty and every distinct recovery check");
            await RejectRecovery("subject", recovered.Revision, "task.recovery_not_required");
            foreach (var cancelled in new[] { false, true })
            {
                var restored = Setup(); var interrupted = restored.Evidence.Operation with { State = InstallationOperationState.Interrupted };
                var waiting = store.RecordReconciliation(restored.Run.RunId, "subject", new("runtime", interrupted, null), 3);
                var witness = new TaskReconciliationEvidence("runtime", interrupted, fixture.Installation with
                { Receipt = new(interrupted.OperationId, cancelled ? RuntimeSwitchOutcome.Cancelled : RuntimeSwitchOutcome.Failed, null) });
                var result = store.RecordRecoveryReconciliation(waiting.RunId, "subject", witness, waiting.Revision);
                Check(result.State == (cancelled ? TaskRunState.Cancelled : TaskRunState.Failed),
                    "restored baseline and original compensation receipt resolve failure/cancellation without pretending success");
            }
            var commit = Setup(); var pending = store.RecordReconciliation(commit.Run.RunId, "subject", commit.Evidence with { Snapshot = null }, 3);
            var failedCommit = new TaskPlanStore(path, protection, beforeCommit: point => { if (point == TaskPlanCommitPoint.RecoveryResultWritten) throw new IOException("injected"); });
            Reject(() => failedCommit.RecordRecoveryReconciliation(pending.RunId, "subject", commit.Evidence, pending.Revision), "task.plan_store_unavailable");
            Check(new TaskPlanStore(path, protection).GetRun(pending.RunId, "subject") is { State: TaskRunState.NeedsRecovery, Revision: 4 },
                "recovery evidence, audit and state roll back atomically on failed commit");
            var unobserved = Setup(); var waitingForHost = store.RecordReconciliation(unobserved.Run.RunId, "subject", unobserved.Evidence with { Snapshot = null }, 3);
            var offline = new Domain { Evidence = unobserved.Evidence, Unavailable = true };
            try { await new TaskRecoveryReconciliationService(store, offline).RecheckAsync(waitingForHost.RunId, "subject", waitingForHost.Revision, default); throw new Exception("Accepted absent host"); }
            catch (RuntimeLifecycleException)
            { Check(store.GetRun(waitingForHost.RunId, "subject")?.Revision == waitingForHost.Revision, "unavailable recovery observation leaves prior evidence intact"); }
            foreach (var transform in transforms.Take(8))
            {
                var current = Setup(); var unknown = store.RecordReconciliation(current.Run.RunId, "subject", current.Evidence with { Snapshot = null }, 3);
                var checkedRun = store.RecordRecoveryReconciliation(unknown.RunId, "subject", transform(current.Evidence), unknown.Revision);
                Check(checkedRun.State == TaskRunState.NeedsRecovery, "recovery does not accept missing, mismatched, drifting or pending host evidence");
            }
            var bounded = Setup(); var boundedRun = store.RecordReconciliation(bounded.Run.RunId, "subject", bounded.Evidence with { Snapshot = null }, 3);
            TaskReconciliationEvidence Unresolved() => bounded.Evidence with { Snapshot = bounded.Evidence.Snapshot! with { PendingOperationId = Guid.NewGuid() } };
            for (var i = 0; i < 32; i++) boundedRun = store.RecordRecoveryReconciliation(boundedRun.RunId, "subject", Unresolved(), boundedRun.Revision);
            Reject(() => store.RecordRecoveryReconciliation(boundedRun.RunId, "subject", Unresolved(), boundedRun.Revision), "task.recovery_check_capacity_exceeded");
            Check(new TaskPlanStore(path, protection).GetRun(boundedRun.RunId, "subject")?.Revision == boundedRun.Revision,
                "bounded recovery journal remains readable without resetting pending tasks");
            var tail = new TaskStep("tail", "runtime.verifyInstallation", 1, "sing-box-read-1",
                TaskPlanJson.FreezeInputs(new VerifyRuntimeInstallationInput(fixture.Installation.Installation.InstallationId, 7)),
                ["runtime"], [], [], [], TaskStepDisposition.NoOp, [], "read-only", "none", "none");
            var partialSemantics = plan.Semantics with { Steps = [.. plan.Semantics.Steps, tail] };
            var partial = Setup(plan with { Semantics = partialSemantics, PlanDigest = TaskPlanCanonicalizer.Digest(partialSemantics) });
            var skipped = store.RecordReconciliation(partial.Run.RunId, "subject", partial.Evidence with { Snapshot = null }, 3);
            var partialResult = store.RecordRecoveryReconciliation(skipped.RunId, "subject", partial.Evidence, skipped.Revision);
            Check(partialResult.State == TaskRunState.NeedsRecovery && partialResult.Steps.Single(item => item.StepId == "tail").State == TaskStepRunState.Skipped,
                "recovery of a committed component cannot silently resume or declare success for skipped downstream work");
            Check(new TaskPlanStore(path, protection).GetRun(partialResult.RunId, "subject")?.State == TaskRunState.NeedsRecovery,
                "reopening preserves incomplete downstream work after a conclusive component receipt");
            var changedConfiguration = Setup();
            var configUnknown = store.RecordReconciliation(changedConfiguration.Run.RunId, "subject", changedConfiguration.Evidence with { Snapshot = null }, 3);
            var configWitness = changedConfiguration.Evidence with { Snapshot = changedConfiguration.Evidence.Snapshot! with { ConfigurationIdentity = "later-configuration" } };
            Check(store.RecordRecoveryReconciliation(configUnknown.RunId, "subject", configWitness, configUnknown.Revision).State == TaskRunState.NeedsRecovery,
                "recovery cannot certify a success using later configuration state");
            foreach (var change in new[] { RuntimeChange.Install, RuntimeChange.Uninstall })
            foreach (var keepData in new[] { false, true })
            {
                if (change == RuntimeChange.Install && !keepData) continue;
                var original = TaskPlanJson.ReadInputs<RuntimeTaskFrozenChange>(plan.Semantics.Steps[0].Inputs);
                var frozenChange = original with { Change = change, ConfigurationIdentity = change == RuntimeChange.Install ? null : new string('e', 64),
                    PreviousReleaseId = change == RuntimeChange.Install ? null : original.PreviousReleaseId,
                    CurrentVersion = change == RuntimeChange.Install ? null : original.CurrentVersion,
                    Release = change == RuntimeChange.Uninstall ? null : original.Release,
                    Impact = change == RuntimeChange.Uninstall ? new(false, true, true, keepData, !keepData, false) : new(false, false, false, true, false, false),
                    Selection = original.Selection with { KeepData = keepData,
                        ExpectedCatalogRevision = change == RuntimeChange.Uninstall ? 0 : original.Selection.ExpectedCatalogRevision,
                        ExpectedTrustRevision = change == RuntimeChange.Uninstall ? 0 : original.Selection.ExpectedTrustRevision,
                        SourceId = change == RuntimeChange.Uninstall ? null : original.Selection.SourceId,
                        ReleaseId = change == RuntimeChange.Uninstall ? null : original.Selection.ReleaseId } };
                var semantics = plan.Semantics with { Steps = [plan.Semantics.Steps[0] with
                    { ActionId = "runtime." + change.ToString().ToLowerInvariant(), RiskClass = change == RuntimeChange.Uninstall && !keepData ? "destructive" : "host-management",
                        Inputs = TaskPlanJson.FreezeInputs(frozenChange) }] };
                var pendingChange = Setup(plan with { Semantics = semantics, PlanDigest = TaskPlanCanonicalizer.Digest(semantics) });
                var waiting = store.RecordReconciliation(pendingChange.Run.RunId, "subject", pendingChange.Evidence with { Snapshot = null }, 3);
                var snapshot = pendingChange.Evidence.Snapshot!;
                if (change == RuntimeChange.Uninstall) snapshot = snapshot with
                {
                    Installation = snapshot.Installation with { Ownership = RuntimeOwnership.Absent, ActiveReleaseId = null },
                    MeasuredVersion = null, ActiveRevisionId = null, ConfigurationIdentity = keepData ? frozenChange.ConfigurationIdentity : null
                };
                snapshot = snapshot with { CommitWitness = new(pendingChange.Evidence.Operation.OperationId, snapshot.Installation,
                    snapshot.ActiveRevisionId, snapshot.ConfigurationIdentity, snapshot.MeasuredVersion) };
                var evidence = pendingChange.Evidence with { Snapshot = snapshot, Operation = pendingChange.Evidence.Operation with { State = InstallationOperationState.Interrupted } };
                Check(store.RecordRecoveryReconciliation(waiting.RunId, "subject", evidence, waiting.Revision).State == TaskRunState.Succeeded,
                    "original committed facts resolve interrupted " + change + " with KeepData=" + keepData);
                Check(new TaskPlanStore(path, protection).GetRun(waiting.RunId, "subject")?.State == TaskRunState.Succeeded,
                    "resolved installation/removal proof remains verifiable after store reopening");
            }
            var missingCommit = Setup();
            var missingCommitRun = store.RecordReconciliation(missingCommit.Run.RunId, "subject", missingCommit.Evidence with { Snapshot = null }, 3);
            Check(store.RecordRecoveryReconciliation(missingCommitRun.RunId, "subject", missingCommit.Evidence with
                { Snapshot = missingCommit.Evidence.Snapshot! with { CommitWitness = null } }, missingCommitRun.Revision).State == TaskRunState.NeedsRecovery,
                "a matching success receipt without the original commit witness cannot resolve an interrupted result");
            var laterState = Setup(); var laterRun = store.RecordReconciliation(laterState.Run.RunId, "subject", laterState.Evidence with { Snapshot = null }, 3);
            Check(store.RecordRecoveryReconciliation(laterRun.RunId, "subject", laterState.Evidence with { Snapshot = laterState.Evidence.Snapshot! with
                { Installation = laterState.Evidence.Snapshot.Installation with { StateRevision = 9 } } }, laterRun.Revision).State == TaskRunState.NeedsRecovery,
                "a subsequent revision with identical version/configuration does not prove the original operation's final state");
            var wrongProof = Setup();
            var malformedProof = wrongProof.Evidence with { Snapshot = wrongProof.Evidence.Snapshot! with { CommitWitness = wrongProof.Evidence.Snapshot.CommitWitness! with
                { Installation = wrongProof.Evidence.Snapshot.CommitWitness!.Installation with { HostId = "unrelated-host" } } } };
            var proofRejected = store.RecordReconciliation(wrongProof.Run.RunId, "subject", malformedProof, 3);
            Check(proofRejected.State == TaskRunState.NeedsRecovery && store.GetRunEvidence(proofRejected.RunId, "subject")?.Observations.Single().Snapshot?.CommitWitness is null,
                "public history withholds a substituted commit host while retaining the original uncertain result");
            Check(store.RecordReconciliation(wrongProof.Run.RunId, "subject", malformedProof, 3).Revision == proofRejected.Revision,
                "redacting an unrelated public commit never rewrites the stored original evidence");

            async Task RejectRecovery(string subject, long revision, string code)
            {
                try { await recoveryService.RecheckAsync(needsRecovery.RunId, subject, revision, default); throw new Exception("Accepted invalid recovery check"); }
                catch (TaskPlanStoreException ex) { Check(ex.ProblemCode == code, "explicit recovery rejects " + code); }
            }
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
