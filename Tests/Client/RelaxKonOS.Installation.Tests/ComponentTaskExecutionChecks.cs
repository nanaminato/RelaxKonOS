using System.Reflection;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Apps.FileServices;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;

internal static class ComponentTaskExecutionChecks
{
    public static async Task RunAsync()
    {
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS: Component task " + label); }
        var root = Path.Combine(Path.GetTempPath(), "component-task-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var actor = Guid.NewGuid();
            var intent = new TaskPlanIntent("app", "relaxkonos.component-center", 1,
                [new("change", "runtime.uninstall", 1, "sing-box-change-1", TaskPlanJson.FreezeInputs(new RuntimeTaskChangeInput(Guid.NewGuid(), "host", 7, 0, 0, null, null, false, true)), [])]);
            var plan = TaskPreviewClientChecks.ChangePlan(intent, actor);
            var store = new ComponentTaskReferenceStore(Path.Combine(root, "tasks"));
            var reference = new ComponentTaskReference(plan, Guid.NewGuid(), "stable-start", null);
            store.Save("server", actor, reference);
            Check(new ComponentTaskReferenceStore(Path.Combine(root, "tasks")).Read("server", actor).Single().StartKey == "stable-start",
                "durable reference reopens with the original plan and stable start key");
            Check(store.Read("another-server", actor).Count == 0 && store.Read("server", Guid.NewGuid()).Count == 0, "references are isolated by server and actor");
            var runId = Guid.NewGuid(); store.Save("server", actor, reference with { RunId = runId });
            foreach (var changed in new[] { reference, reference with { RunId = runId, StartKey = "different" } })
            {
                try { store.Save("server", actor, changed); throw new Exception("Replaced stable start identity"); }
                catch (InvalidDataException) { Check(true, "known run identity and original start key cannot be overwritten"); }
            }
            var runtimeClient = DispatchProxy.Create<IRuntimeCatalogClient, ReattachmentRuntime>();
            ((ReattachmentRuntime)(object)runtimeClient).Operation = ((ReattachmentRuntime)(object)runtimeClient).Operation with
                { State = RelaxKonOS.Protocol.Installations.InstallationOperationState.Succeeded };
            using var runtime = new ProxyServerRuntimeViewModel(runtimeClient) { CanRead = true, CanManage = true }; await runtime.LoadAsync();
            var lostStore = new ComponentTaskReferenceStore(Path.Combine(root, "lost"));
            var execution = new Execution(lostStore, actor, Check) { LoseStartResponse = true };
            using (var model = new ComponentCenterViewModel(runtime, new(), "server", actor, execution: execution, references: lostStore))
            {
                model.FrozenChangePlan = plan; model.ConfirmTaskAsync = _ => Task.FromResult(false);
                await model.ApproveTaskCommand.ExecuteAsync(null);
                Check(execution.Approvals == 0 && execution.Starts == 0, "dismissing confirmation never approves or starts");
                model.ConfirmTaskAsync = _ => { runtime.RefreshAccess = () => runtime.CanManage = false; return Task.FromResult(true); };
                await model.ApproveTaskCommand.ExecuteAsync(null);
                Check(execution.Approvals == 0 && execution.Starts == 0,
                    "management permission revoked during confirmation prevents approval");
                runtime.RefreshAccess = null;
                runtime.CanManage = true;
                model.ConfirmTaskAsync = _ => Task.FromResult(true);
                var prompts = 0;
                model.TaskCredentialsAsync = () => { prompts++; return Task.FromResult<HostAdministratorCredentials?>(new("administrator", "fixture-secret")); };
                await model.ApproveTaskCommand.ExecuteAsync(null);
                Check(prompts == 1 && execution.Approvals == 2 && execution.Starts == 1 && model.CurrentTaskRun is null,
                    "standard-user flow prompts once and lost start response does not invent a running task");
                Check(lostStore.Read("server", actor).Single().RunId is null, "pending start reference is retained when its response is lost");
            }
            using (var reopened = new ComponentCenterViewModel(runtime, new(), "server", actor, execution: execution, references: lostStore))
            {
                await reopened.TickTaskAsync();
                Check(execution.Starts == 1 && execution.ApprovalReads == 1 && reopened.CurrentTaskRun?.RunId == execution.Run!.RunId,
                    "reopening finds the accepted run by read-only approval lookup instead of starting again");
                Check(lostStore.Read("server", actor).Single().RunId == execution.Run!.RunId, "recovered run identity is durably saved");
                await reopened.TickTaskAsync();
                Check(execution.Advances == 0, "reopening and polling do not automatically resume queued work");
                reopened.ConfirmTaskAsync = _ => Task.FromResult(true);
                await reopened.ResumeTaskCommand.ExecuteAsync(null); await reopened.TickTaskAsync();
                Check(execution.Advances == 1 && reopened.CurrentTaskRun?.State == TaskRunState.Running, "explicit continuation resumes the original approved run");
                runtime.CanRead = false;
                var reads = execution.Reads;
                await reopened.TickTaskAsync();
                await reopened.RefreshTaskCommand.ExecuteAsync(null);
                Check(execution.Reads == reads && execution.Advances == 1 && !reopened.CanRefreshTask && !reopened.CanCancelTask,
                    "revoked read permission blocks history requests and task writes");
                runtime.CanRead = true;
                await reopened.TickTaskAsync();
                Check(execution.Advances == 1, "restored read permission does not silently resume execution");
                await reopened.ResumeTaskCommand.ExecuteAsync(null);
                runtime.RefreshAccess = () => runtime.CanManage = false;
                await reopened.TickTaskAsync();
                Check(execution.Reads == reads + 2 && execution.Advances == 1,
                    "revoked management permission switches active execution to read-only observation");
                runtime.RefreshAccess = null;
                runtime.CanManage = true;
                await reopened.TickTaskAsync();
                Check(execution.Advances == 1, "restored management permission does not silently resume execution");
                await reopened.CancelTaskCommand.ExecuteAsync(null);
                Check(reopened.CurrentTaskRun?.State == TaskRunState.Cancelling && execution.Cancels == 1, "cancel shows a request rather than completed cancellation");
                using (var observer = new ComponentCenterViewModel(runtime, new(), "server", actor, execution: execution, references: lostStore))
                {
                    await observer.TickTaskAsync(); await observer.TickTaskAsync();
                    Check(execution.Advances == 1 && observer.CurrentTaskRun?.State == TaskRunState.Cancelling && observer.CanResumeTask,
                        "reopened cancellation remains read-only and offers explicit continuation");
                    observer.ConfirmTaskAsync = _ => Task.FromResult(true);
                    await observer.ResumeTaskCommand.ExecuteAsync(null); await observer.TickTaskAsync();
                    Check(execution.Advances == 2 && observer.CurrentTaskRun?.State == TaskRunState.Cancelled,
                        "explicit continuation delivers cancellation for a reopened task");
                }
                await reopened.TickTaskAsync();
                Check(reopened.CurrentTaskRun?.State == TaskRunState.Cancelled, "receipt-driven advancement supplies the eventual terminal cancellation");
                Check(reopened.ShowTaskResult && reopened.TaskSteps.Single().HasOperation
                    && reopened.TaskSteps.Single().Operation.Resolve().Contains(reopened.CurrentTaskRun!.Steps[0].DomainOperationId!.Value.ToString("D"), StringComparison.Ordinal),
                    "terminal results expose the host-bound original domain operation for investigation");
                Check(reopened.TaskSteps.Single().DataImpact.Resolve() == LocalizedText.Get("component_center.result_planned_preserve"),
                    "cancelled results label data retention as a frozen plan rather than observed success");
                var uncertain = execution.Run! with { State = TaskRunState.NeedsRecovery,
                    Steps = [execution.Run.Steps[0] with { State = TaskStepRunState.NeedsRecovery }] };
                execution.Run = uncertain; reopened.CurrentTaskRun = uncertain; runtime.CanManage = false;
                var advances = execution.Advances;
                await reopened.TickTaskAsync();
                Check(execution.Rechecks == 0, "ordinary polling never requests a recovery check");
                await reopened.RecheckTaskRecoveryCommand.ExecuteAsync(null);
                Check(reopened.CurrentTaskRun?.State == TaskRunState.NeedsRecovery && execution.Rechecks == 1 && execution.Advances == advances,
                    "read-only observer can explicitly recheck unresolved recovery without advancing execution");
                execution.Run = uncertain with { State = TaskRunState.Succeeded, Steps = [uncertain.Steps[0] with { State = TaskStepRunState.Succeeded }] };
                await reopened.RecheckTaskRecoveryCommand.ExecuteAsync(null);
                Check(reopened.CurrentTaskRun?.State == TaskRunState.Succeeded && !reopened.CanRecheckTaskRecovery && execution.Starts == 1,
                    "conclusive recovery result withdraws the recheck action and never starts another run");
                runtime.CanManage = true;
                execution.Run = uncertain; reopened.CurrentTaskRun = uncertain;
                var operation = uncertain.Steps[0].DomainOperationId!.Value;
                Check(execution.EvidenceReads == 0, "ordinary task polling never fetches historical evidence");
                await reopened.ReadTaskEvidenceCommand.ExecuteAsync(null);
                Check(execution.EvidenceReads == 1 && reopened.TaskEvidence is [{ Evidence.Snapshot: null }],
                    "explicit historical evidence read exposes missing observation without running recovery");
                execution.PendingEvidence = new();
                var evidenceRead = reopened.ReadTaskEvidenceCommand.ExecuteAsync(null);
                reopened.CurrentTaskRun = uncertain with { Revision = uncertain.Revision + 1 };
                execution.PendingEvidence.SetResult(new(uncertain, []));
                await evidenceRead;
                Check(reopened.CurrentTaskRun.Revision == uncertain.Revision + 1 && reopened.TaskEvidence.Count == 0,
                    "late evidence response cannot replace a newer task observation");
                execution.PendingEvidence = null;
                reopened.CurrentTaskRun = uncertain;
                await reopened.ReadTaskEvidenceCommand.ExecuteAsync(null);
                execution.Recovery = new(uncertain, new(true, operation, RuntimeChange.Uninstall,
                    RelaxKonOS.Protocol.Installations.InstallationStage.Failed, true, "runtime.failed", "runtime.compensation_failed",
                    "target", "previous", new(true, true, false)), []);
                await reopened.ReadTaskRecoveryCommand.ExecuteAsync(null);
                Check(reopened.CanRetryTaskCompensation && reopened.CanStopTaskRecovery && !reopened.CanCloseTaskRecovery,
                    "running service permits compensation and emergency stop but never close");
                reopened.ConfirmTaskRecoveryAsync = (_, _) => Task.FromResult(false);
                await reopened.RetryTaskCompensationCommand.ExecuteAsync(null);
                Check(execution.Recoveries == 0, "dismissed recovery confirmation sends no command");
                reopened.ConfirmTaskRecoveryAsync = (_, _) => { runtime.CanManage = false; return Task.FromResult(true); };
                await reopened.StopTaskRecoveryCommand.ExecuteAsync(null);
                Check(execution.Recoveries == 0, "revoked management during recovery confirmation blocks the command");
                runtime.CanManage = true;
                reopened.ConfirmTaskRecoveryAsync = (_, _) => Task.FromResult(true);
                reopened.TaskCredentialsAsync = () => { runtime.CanManage = false; return Task.FromResult<HostAdministratorCredentials?>(new("admin", "fixture-secret")); };
                await reopened.StopTaskRecoveryCommand.ExecuteAsync(null);
                Check(execution.Recoveries == 1, "revoked management during credential prompt prevents the authenticated retry");
                runtime.CanManage = true;
                await reopened.ReadTaskRecoveryCommand.ExecuteAsync(null);
                reopened.TaskCredentialsAsync = () => Task.FromResult<HostAdministratorCredentials?>(new("admin", "fixture-secret"));
                await reopened.StopTaskRecoveryCommand.ExecuteAsync(null);
                Check(execution.Recoveries == 3 && execution.RecoveryKeys.Distinct().Count() == 1,
                    "recovery authentication retries retain the original request identity");
                Check(reopened.CanCloseTaskRecovery && !reopened.CanStopTaskRecovery && reopened.RecoveryAttempts.Count == 1,
                    "fresh stopped-service observation enables close and exposes the recorded attempt");
                Check(reopened.CurrentTaskRun?.State == TaskRunState.NeedsRecovery && execution.Advances == advances,
                    "recovery response never invents task success or resumes remaining steps");
                runtime.CanRead = false;
                Check(reopened.TaskRecovery is null && reopened.RecoveryAttempts.Count == 0 && !reopened.CanRetryTaskCompensation,
                    "read revocation clears recovery details and action authority");
                Check(reopened.TaskEvidence.Count == 0 && !reopened.CanReadTaskEvidence, "read revocation clears historical evidence and blocks its query");
                runtime.CanRead = true;
                Check(!reopened.CanRetryTaskCompensation, "restored permission requires fresh recovery details");
                runtime.CanManage = true;
                await reopened.ReadTaskRecoveryCommand.ExecuteAsync(null);
                await reopened.CloseTaskRecoveryCommand.ExecuteAsync(null);
                await reopened.RetryTaskCompensationCommand.ExecuteAsync(null);
                Check(reopened.RecoveryAttempts.Single().Attempt.Action == TaskRecoveryAction.RetryCompensation
                    && execution.Recoveries == 7 && execution.Advances == advances,
                    "close and compensation use separately confirmed actions without forward execution");
                execution.Recovery = execution.Recovery with { Recovery = execution.Recovery.Recovery with { ServiceProblem = "runtime.service_unavailable" } };
                await reopened.ReadTaskRecoveryCommand.ExecuteAsync(null);
                Check(!reopened.CanCloseTaskRecovery, "service observation failure blocks close");
                var pendingRecovery = new TaskCompletionSource<TaskRecoveryView>();
                execution.PendingRecovery = pendingRecovery.Task;
                var readingRecovery = reopened.ReadTaskRecoveryCommand.ExecuteAsync(null);
                reopened.SelectedTask = null;
                pendingRecovery.SetResult(execution.Recovery);
                await readingRecovery;
                Check(reopened.TaskRecovery is null && reopened.CurrentTaskRun is null,
                    "late recovery read cannot overwrite a different task selection");
                Check(!reopened.ShowTaskResult && reopened.TaskSteps.Count == 0, "changing task selection clears the previous result and step evidence");
            }
            foreach (var revoke in new[] { false, true })
            {
                var raceStore = new ComponentTaskReferenceStore(Path.Combine(root, "race-" + revoke));
                var raceRun = execution.Run! with { RunId = Guid.NewGuid(), ApprovalId = Guid.NewGuid(), Revision = 3,
                    State = TaskRunState.Running, CancellationRequested = false, Steps = [execution.Run!.Steps[0] with { State = TaskStepRunState.Running }] };
                raceStore.Save("server", actor, new(plan, raceRun.ApprovalId, "race-start", raceRun.RunId));
                var raced = new Execution(raceStore, actor, Check) { Run = raceRun, RevisionConflictOnce = true,
                    DuringRefresh = revoke ? () => runtime.CanManage = false : null };
                using var raceModel = new ComponentCenterViewModel(runtime, new(), "server", actor, execution: raced, references: raceStore)
                    { CurrentTaskRun = raceRun, ConfirmTaskAsync = _ => Task.FromResult(true) };
                await raceModel.ResumeTaskCommand.ExecuteAsync(null);
                await raceModel.TickTaskAsync();
                Check(raced.Advances == 1 && raced.Reads == 1 && raceModel.CurrentTaskRun?.Revision == 4,
                    "worker revision conflict performs one read and never repeats a write in the same tick");
                runtime.CanManage = true;
                await raceModel.TickTaskAsync();
                Check(raced.Advances == (revoke ? 1 : 2), revoke
                    ? "revocation during conflict resynchronization clears intent and restoration cannot resume it"
                    : "forward worker observation preserves the already explicit continuation for the next tick");
            }
            var blockedPath = Path.Combine(root, "blocked"); File.WriteAllText(blockedPath, "not a directory");
            var blocked = new ComponentTaskReferenceStore(blockedPath); var prevented = new Execution(blocked, actor, Check) { NeedCredentials = false };
            using (var model = new ComponentCenterViewModel(runtime, new(), "server", actor, execution: prevented, references: blocked))
            {
                model.FrozenChangePlan = plan; model.ConfirmTaskAsync = _ => Task.FromResult(true);
                await model.ApproveTaskCommand.ExecuteAsync(null);
                Check(prevented.Starts == 0 && model.TaskNotice.Resolve() == LocalizedText.Get("component_center.task_reference_failed"),
                    "reference persistence failure prevents the start request");
            }
            var file = Directory.EnumerateFiles(Path.Combine(root, "tasks"), "*.json", SearchOption.AllDirectories).Single();
            var contents = File.ReadAllText(file);
            Check(!contents.Contains("fixture-secret", StringComparison.Ordinal) && !contents.Contains("password", StringComparison.OrdinalIgnoreCase),
                "local task references contain no administrator credentials");
            File.WriteAllText(file, contents.Replace("\"schema\":1", "\"schema\":0", StringComparison.Ordinal));
            try { store.Read("server", actor); throw new Exception("Read unsupported local reference schema"); }
            catch (InvalidDataException) { Check(File.Exists(file), "unsupported reference format fails closed and preserves the file"); }
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class Execution(ComponentTaskReferenceStore store, Guid actor, Action<bool, string> check) : ITaskExecutionClient
    {
        public bool LoseStartResponse, NeedCredentials = true;
        public bool RevisionConflictOnce;
        public Action? DuringRefresh;
        public int Approvals, Starts, ApprovalReads, Advances, Cancels, Reads, Rechecks;
        public int Recoveries;
        public int EvidenceReads;
        public TaskCompletionSource<TaskRunEvidence>? PendingEvidence;
        public TaskRecoveryView? Recovery;
        public Task<TaskRecoveryView>? PendingRecovery;
        public List<string> RecoveryKeys = [];
        public TaskRun? Run; private TaskPlanApproval? _approval;
        public Task<TaskRunEvidence> ReadEvidenceAsync(TaskPlan plan, TaskRun run, CancellationToken ct)
        {
            EvidenceReads++;
            if (PendingEvidence is not null) return PendingEvidence.Task;
            return Task.FromResult(new TaskRunEvidence(run, [new(run.Steps[0].StepId, run.Steps[0].DomainOperationId!.Value,
                RelaxKonOS.Protocol.Installations.InstallationOperationState.Interrupted, false, null)]));
        }
        public Task<TaskPlanApproval> ApproveAsync(TaskPlan plan, ApproveTaskPlanRequest request, string key, CancellationToken ct)
        {
            Approvals++;
            if (NeedCredentials && request.Password is null) return Task.FromException<TaskPlanApproval>(new HttpRequestException("task.administrator_authentication_failed"));
            var now = plan.CreatedAt.AddSeconds(1);
            return Task.FromResult(_approval = new(Guid.NewGuid(), plan.PlanId, plan.PlanDigest, TaskApprovalState.Granted, 1, actor.ToString("D"), "session", "host",
                "linux:0", "fixture", "policy", now, now.AddMinutes(1), now.AddMinutes(5), null, [new("SingBoxInstall", "singbox")], null));
        }
        public Task<TaskRun> StartAsync(TaskPlan plan, TaskPlanApproval approval, string key, CancellationToken ct)
        {
            Starts++; var saved = store.Read("server", actor).Single(item => item.ApprovalId == approval.ApprovalId);
            check(saved.StartKey == key && saved.Plan.PlanDigest == plan.PlanDigest, "stable reference is persisted before any start request");
            Run ??= new(Guid.NewGuid(), plan.PlanId, plan.PlanDigest, approval.ApprovalId, actor.ToString("D"), "session", "host", "policy", TaskRunState.Queued,
                1, false, approval.CreatedAt.AddSeconds(1), [new(plan.Semantics.Steps[0].StepId, TaskStepRunState.Pending, new string('a', 64), null, null, null)], null);
            return LoseStartResponse ? Task.FromException<TaskRun>(new HttpRequestException("task.request_failed")) : Task.FromResult(Run);
        }
        public Task<TaskPlanApproval> ReadApprovalAsync(TaskPlan plan, Guid approvalId, CancellationToken ct)
        { ApprovalReads++; return Task.FromResult(_approval! with { State = TaskApprovalState.Consumed, Revision = 2, RunId = Run!.RunId }); }
        public Task<TaskRun> ReadAsync(TaskPlan plan, Guid runId, CancellationToken ct) { Reads++; return Task.FromResult(Run!); }
        public Task<TaskRun> RefreshAsync(TaskPlan plan, TaskRun run, CancellationToken ct) { Reads++; DuringRefresh?.Invoke(); return Task.FromResult(Run!); }
        public Task<TaskRun> RecheckRecoveryAsync(TaskPlan plan, TaskRun run, CancellationToken ct) { Rechecks++; return Task.FromResult(Run!); }
        public Task<TaskRecoveryView> ReadRecoveryAsync(TaskPlan plan, TaskRun run, CancellationToken ct) => PendingRecovery ?? Task.FromResult(Recovery!);
        public Task<TaskRecoveryResponse> RecoverAsync(TaskPlan plan, TaskRun run, TaskRecoveryRequest request, string key, CancellationToken ct)
        {
            Recoveries++; RecoveryKeys.Add(key);
            if (request.Password is null) return Task.FromException<TaskRecoveryResponse>(new HttpRequestException("task.administrator_authentication_failed"));
            check(request.Confirmed && request.OperationId == run.Steps[0].DomainOperationId && request.ExpectedRevision == run.Revision,
                "recovery command binds explicit confirmation and the original operation and revision");
            var attempt = new TaskRecoveryAttempt(Guid.NewGuid(), run.RunId, run.Steps[0].StepId, request.OperationId, run.Revision,
                request.Action, run.CanonicalSubject, "session", run.HostId, "policy", "linux:0", "fixture", run.CreatedAt.AddSeconds(1),
                run.CreatedAt.AddMinutes(1), true, null);
            Recovery = Recovery! with { Recovery = Recovery.Recovery with { Service = new(true, false, false) }, Attempts = [attempt] };
            return Task.FromResult(new TaskRecoveryResponse(run, attempt));
        }
        public Task<TaskRun> AdvanceAsync(TaskPlan plan, TaskRun run, CancellationToken ct)
        {
            Advances++;
            if (RevisionConflictOnce)
            {
                RevisionConflictOnce = false; Run = run with { Revision = run.Revision + 1 };
                return Task.FromException<TaskRun>(new HttpRequestException("task.run_revision_conflict"));
            }
            Run = run.State == TaskRunState.Queued ? run with { State = TaskRunState.Running, Revision = 3, Steps = [run.Steps[0] with
                { State = TaskStepRunState.Running, DomainOperationId = Guid.NewGuid(), RequestDigest = new string('b', 64) }] }
                : run with { State = TaskRunState.Cancelled, Revision = 5, Steps = [run.Steps[0] with { State = TaskStepRunState.Cancelled }] };
            return Task.FromResult(Run);
        }
        public Task<TaskRun> CancelAsync(TaskPlan plan, TaskRun run, CancellationToken ct)
        { Cancels++; return Task.FromResult(Run = run with { State = TaskRunState.Cancelling, Revision = 4, CancellationRequested = true }); }
    }
}
