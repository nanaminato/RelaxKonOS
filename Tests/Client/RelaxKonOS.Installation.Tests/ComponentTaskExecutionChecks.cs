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
                await reopened.CancelTaskCommand.ExecuteAsync(null);
                Check(reopened.CurrentTaskRun?.State == TaskRunState.Cancelling && execution.Cancels == 1, "cancel shows a request rather than completed cancellation");
                await reopened.TickTaskAsync();
                Check(reopened.CurrentTaskRun?.State == TaskRunState.Cancelled, "receipt-driven advancement supplies the eventual terminal cancellation");
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
        public int Approvals, Starts, ApprovalReads, Advances, Cancels;
        public TaskRun? Run; private TaskPlanApproval? _approval;
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
        public Task<TaskRun> ReadAsync(TaskPlan plan, Guid runId, CancellationToken ct) => Task.FromResult(Run!);
        public Task<TaskRun> RefreshAsync(TaskPlan plan, TaskRun run, CancellationToken ct) => Task.FromResult(Run!);
        public Task<TaskRun> AdvanceAsync(TaskPlan plan, TaskRun run, CancellationToken ct)
        {
            Advances++;
            Run = run.State == TaskRunState.Queued ? run with { State = TaskRunState.Running, Revision = 3, Steps = [run.Steps[0] with
                { State = TaskStepRunState.Running, DomainOperationId = Guid.NewGuid(), RequestDigest = new string('b', 64) }] }
                : run with { State = TaskRunState.Cancelled, Revision = 5, Steps = [run.Steps[0] with { State = TaskStepRunState.Cancelled }] };
            return Task.FromResult(Run);
        }
        public Task<TaskRun> CancelAsync(TaskPlan plan, TaskRun run, CancellationToken ct)
        { Cancels++; return Task.FromResult(Run = run with { State = TaskRunState.Cancelling, Revision = 4, CancellationRequested = true }); }
    }
}
