using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.TaskOrchestration;
using System.Collections.Immutable;

internal static class ComponentCenterWorkspaceChecks
{
    public static void Run(ShellSettings settings)
    {
        var languageBefore = settings.Language;
        var appearanceBefore = settings.Appearance;
        var output = Path.Combine(AppContext.BaseDirectory, "preview-qa", "component-center");
        Directory.CreateDirectory(output);
        try
        {
            foreach (var language in new[] { "zh-CN", "en-US", "ja-JP" })
            foreach (var dark in new[] { false, true })
            {
                settings.Language = language;
                settings.Appearance = settings.Appearance with { Mode = dark ? ThemeKind.Dark : ThemeKind.Light };
                using var runtime = new ProxyServerRuntimeViewModel(DispatchProxy.Create<IRuntimeCatalogClient, DisplayOnlyRuntime>());
                using var model = new ComponentCenterViewModel(runtime, new(), "https://host.example/", Guid.NewGuid());
                model.Stage(new("sing-box", "sing-box", "host-a", Guid.NewGuid(), "source-a", "release-a", "1.14.3", RuntimeChange.Repair, true, false));
                var view = new ComponentCenterWorkspace(model);
                var window = new Window { Width = 1060, Height = 800, Content = view };
                window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var search = view.FindControl<TextBox>("Search")!;
                search.Text = "missing"; Dispatcher.UIThread.RunJobs();
                Check(view.FindControl<TreeView>("ComponentTree")!.ItemCount == 0, "Search hides nonmatching components");
                search.Text = "SING-BOX"; Dispatcher.UIThread.RunJobs();
                Check(view.FindControl<TreeView>("ComponentTree")!.ItemCount == 1, "Search matches component identity without case");
                Check(!model.CanPreview, "An unknown installation cannot execute a saved draft");
                var footer = view.FindControl<Border>("DraftFooter")!;
                var footerOrigin = footer.TranslatePoint(default, view)!.Value;
                Check(footerOrigin.Y >= 0 && footerOrigin.Y + footer.Bounds.Height <= view.Bounds.Height + 1,
                    "The draft footer stays inside the window");
                Check(!view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("component_center.", StringComparison.Ordinal) == true
                    || t.Text?.Contains("proxy_server.", StringComparison.Ordinal) == true),
                    "All visible component-center text resolves in " + language);
                search.Text = "";
                foreach (var page in Enum.GetValues<ComponentCenterPage>())
                {
                    model.Navigate(page); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                    Check(model.Drafts.Count == 1, "Page navigation keeps local intent");
                    using var bitmap = new RenderTargetBitmap(new PixelSize(1060, 800));
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(output, language + (dark ? "-dark-" : "-light-") + page + ".png"), new PngBitmapEncoderOptions());
                }
                window.Close(); Dispatcher.UIThread.RunJobs();
                var activeClient = DispatchProxy.Create<IRuntimeCatalogClient, DisplayOnlyRuntime>();
                ((DisplayOnlyRuntime)(object)activeClient).Active = true;
                using var activeRuntime = new ProxyServerRuntimeViewModel(activeClient) { CanRead = true, CanManage = true };
                activeRuntime.LoadAsync().GetAwaiter().GetResult();
                using var activeModel = new ComponentCenterViewModel(activeRuntime, new(), "https://host.example/", Guid.NewGuid(), new InspectionPreview());
                activeModel.Navigate(ComponentCenterPage.Tasks);
                var activeView = new ComponentCenterWorkspace(activeModel);
                var activeWindow = new Window { Width = 1060, Height = 800, Content = activeView };
                activeWindow.Show(); Dispatcher.UIThread.RunJobs(); activeWindow.UpdateLayout();
                Check(activeRuntime.CanCancelDownload && activeRuntime.OperationProgress == 42,
                    "Reattached task exposes host stage progress and cancellation");
                Check(!activeView.GetVisualDescendants().OfType<TextBlock>().Any(t =>
                    t.Text?.Contains("component_center.", StringComparison.Ordinal) == true
                    || t.Text?.Contains("installation.stage.", StringComparison.Ordinal) == true),
                    "Reattached task text resolves in " + language);
                using (var bitmap = new RenderTargetBitmap(new PixelSize(1060, 800)))
                {
                    bitmap.Render(activeWindow);
                    bitmap.Save(Path.Combine(output, language + (dark ? "-dark-" : "-light-") + "ActiveTask.png"), new PngBitmapEncoderOptions());
                }
                activeWindow.Close(); Dispatcher.UIThread.RunJobs();
                activeModel.Navigate(ComponentCenterPage.All);
                activeModel.InspectCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                var inspectionView = new ComponentCenterWorkspace(activeModel);
                var inspectionWindow = new Window { Width = 1060, Height = 800, Content = inspectionView };
                inspectionWindow.Show(); Dispatcher.UIThread.RunJobs(); inspectionWindow.UpdateLayout();
                Check(activeModel.InspectionSteps.Count == 1 && activeModel.ShowInspection,
                    "Read-only plan results are displayed in component details");
                Check(!inspectionView.GetVisualDescendants().OfType<TextBlock>().Any(t =>
                    t.Text?.Contains("component_center.", StringComparison.Ordinal) == true
                    || t.Text?.StartsWith("runtime.", StringComparison.Ordinal) == true),
                    "Inspection titles and conflicts resolve in " + language);
                using (var bitmap = new RenderTargetBitmap(new PixelSize(1060, 800)))
                {
                    bitmap.Render(inspectionWindow);
                    bitmap.Save(Path.Combine(output, language + (dark ? "-dark-" : "-light-") + "Inspection.png"), new PngBitmapEncoderOptions());
                }
                inspectionWindow.Close(); Dispatcher.UIThread.RunJobs();
                var state = activeRuntime.Installation!.Installation;
                activeModel.Stage(new("sing-box", "sing-box", state.HostId, state.InstallationId,
                    null, null, null, RuntimeChange.Uninstall, false, false));
                var selection = new RuntimeTaskChangeInput(state.InstallationId, state.HostId, state.StateRevision,
                    0, 0, null, null, false, false);
                activeModel.ChangePlanSteps.Add(new(new("change-sing-box", "runtime.uninstall", 1, "sing-box-change-1",
                    TaskPlanJson.FreezeInputs(new RuntimeTaskFrozenChange("sing-box", RuntimeChange.Uninstall, selection,
                        null, "current", "1.14.3", null, new(false, true, true, false, true, false), null)), [], [], [],
                    [new("SingBoxInstall", "singbox")], TaskStepDisposition.Ready, [], "destructive", "domain-idempotent", "domain-transaction")));
                activeModel.ChangePlanDigest = new string('a', 64);
                activeModel.ChangePlanNotice = RelaxKonOS.Client.Localization.LocalizedStatus.Key("component_center.change_plan_ready");
                var changeView = new ComponentCenterWorkspace(activeModel);
                var changeWindow = new Window { Width = 1060, Height = 800, Content = changeView };
                changeWindow.Show(); Dispatcher.UIThread.RunJobs(); changeWindow.UpdateLayout();
                changeView.FindControl<ScrollViewer>("DetailsScroll")!.Offset = new Vector(0, 300);
                Dispatcher.UIThread.RunJobs(); changeWindow.UpdateLayout();
                Check(!changeView.GetVisualDescendants().OfType<TextBlock>().Any(t =>
                    t.Text?.Contains("component_center.", StringComparison.Ordinal) == true
                    || t.Text?.Contains("proxy_server.runtime.action.", StringComparison.Ordinal) == true),
                    "Frozen change and destructive data impact resolve in " + language);
                using (var bitmap = new RenderTargetBitmap(new PixelSize(1060, 800)))
                {
                    bitmap.Render(changeWindow);
                    bitmap.Save(Path.Combine(output, language + (dark ? "-dark-" : "-light-") + "ChangePlan.png"), new PngBitmapEncoderOptions());
                }
                changeWindow.Close(); Dispatcher.UIThread.RunJobs();
                var actor = Guid.NewGuid();
                var semantics = new TaskPlanSemantics(state.HostId, actor.ToString("D"), "app", "relaxkonos.component-center", 1,
                    [activeModel.ChangePlanSteps.Single().Step], "stop-on-failure", "safe-boundary", false, TaskPlanLimits.Default);
                var taskPlan = new TaskPlan(Guid.NewGuid(), DateTimeOffset.UtcNow, TaskPlanCanonicalizer.Digest(semantics), semantics);
                using var taskModel = new ComponentCenterViewModel(activeRuntime, new(), "https://host.example/", actor, execution: new LayoutTaskTransport());
                var runId = Guid.NewGuid();
                var reference = new ComponentTaskHistoryItem(new(taskPlan, Guid.NewGuid(), "layout-only", runId));
                taskModel.TaskHistory.Add(reference); taskModel.SelectedTask = reference;
                taskModel.CurrentTaskRun = new(runId, taskPlan.PlanId, taskPlan.PlanDigest, reference.Reference.ApprovalId, actor.ToString("D"),
                    "layout-session", state.HostId, "layout-policy", TaskRunState.Cancelling, 4, true, DateTimeOffset.UtcNow,
                    [new(semantics.Steps[0].StepId, TaskStepRunState.Running, new string('a', 64), new string('b', 64), activeRuntime.Installation.PendingOperationId, null)], null);
                taskModel.Navigate(ComponentCenterPage.Tasks);
                var taskView = new ComponentCenterWorkspace(taskModel); var taskWindow = new Window { Width = 1060, Height = 800, Content = taskView };
                taskWindow.Show(); Dispatcher.UIThread.RunJobs(); taskWindow.UpdateLayout();
                Check(!taskView.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("component_center.", StringComparison.Ordinal) == true),
                    "Task history, cancellation status and steps resolve in " + language);
                using (var bitmap = new RenderTargetBitmap(new PixelSize(1060, 800)))
                {
                    bitmap.Render(taskWindow); bitmap.Save(Path.Combine(output, language + (dark ? "-dark-" : "-light-") + "TaskExecution.png"), new PngBitmapEncoderOptions());
                }
                taskWindow.Close(); Dispatcher.UIThread.RunJobs();
                taskModel.CurrentTaskRun = taskModel.CurrentTaskRun with { State = TaskRunState.NeedsRecovery,
                    Steps = [taskModel.CurrentTaskRun.Steps[0] with { State = TaskStepRunState.NeedsRecovery }] };
                taskModel.TaskNotice = RelaxKonOS.Client.Localization.LocalizedStatus.Key("component_center.task_recovery_unresolved");
                var recoveryView = new ComponentCenterWorkspace(taskModel); var recoveryWindow = new Window { Width = 1060, Height = 800, Content = recoveryView };
                recoveryWindow.Show(); Dispatcher.UIThread.RunJobs(); recoveryWindow.UpdateLayout();
                Check(taskModel.CanRecheckTaskRecovery && !taskModel.ShowTaskDomainProgress && !recoveryView.GetVisualDescendants().OfType<TextBlock>().Any(text =>
                    text.Text?.Contains("component_center.", StringComparison.Ordinal) == true), "Recovery recheck and guidance resolve in " + language);
                using (var bitmap = new RenderTargetBitmap(new PixelSize(1060, 800)))
                {
                    bitmap.Render(recoveryWindow); bitmap.Save(Path.Combine(output, language + (dark ? "-dark-" : "-light-") + "TaskRecovery.png"), new PngBitmapEncoderOptions());
                }
                recoveryWindow.Close(); Dispatcher.UIThread.RunJobs();
                var modal = Activator.CreateInstance(typeof(RelaxKonOS.WindowManager.ModalDialog<bool>), BindingFlags.Instance | BindingFlags.NonPublic,
                    null, [null, null], null)!;
                var confirmation = (Control)Activator.CreateInstance(typeof(ComponentCenterWorkspace).Assembly.GetType(
                    "RelaxKonOS.Client.Apps.ComponentCenter.TaskPlanConfirmationView")!, [modal, taskPlan])!;
                var confirmWindow = new Window { Width = 720, Height = 600, Content = confirmation };
                confirmWindow.Show(); Dispatcher.UIThread.RunJobs(); confirmWindow.UpdateLayout();
                Check(!confirmation.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("component_center.", StringComparison.Ordinal) == true),
                    "Approval disclosure resolves in " + language);
                using (var bitmap = new RenderTargetBitmap(new PixelSize(720, 600)))
                {
                    bitmap.Render(confirmWindow); bitmap.Save(Path.Combine(output, language + (dark ? "-dark-" : "-light-") + "TaskApproval.png"), new PngBitmapEncoderOptions());
                }
                confirmWindow.Close(); Dispatcher.UIThread.RunJobs();
            }
        }
        finally { settings.Language = languageBefore; settings.Appearance = appearanceBefore; }
        Console.WriteLine("Component Center layout checks passed (3 languages × 2 themes). Previews: " + output);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class LayoutTaskTransport : ITaskExecutionClient
{
    public Task<TaskPlanApproval> ApproveAsync(TaskPlan plan, ApproveTaskPlanRequest request, string key, CancellationToken ct) => throw new NotSupportedException();
    public Task<TaskPlanApproval> ReadApprovalAsync(TaskPlan plan, Guid id, CancellationToken ct) => throw new NotSupportedException();
    public Task<TaskRun> StartAsync(TaskPlan plan, TaskPlanApproval approval, string key, CancellationToken ct) => throw new NotSupportedException();
    public Task<TaskRun> ReadAsync(TaskPlan plan, Guid id, CancellationToken ct) => throw new NotSupportedException();
    public Task<TaskRun> RefreshAsync(TaskPlan plan, TaskRun run, CancellationToken ct) => throw new NotSupportedException();
    public Task<TaskRun> AdvanceAsync(TaskPlan plan, TaskRun run, CancellationToken ct) => throw new NotSupportedException();
    public Task<TaskRun> CancelAsync(TaskPlan plan, TaskRun run, CancellationToken ct) => throw new NotSupportedException();
    public Task<TaskRun> RecheckRecoveryAsync(TaskPlan plan, TaskRun run, CancellationToken ct) => throw new NotSupportedException();
    public Task<TaskRecoveryView> ReadRecoveryAsync(TaskPlan plan, TaskRun run, CancellationToken ct) => throw new NotSupportedException();
    public Task<TaskRecoveryResponse> RecoverAsync(TaskPlan plan, TaskRun run, TaskRecoveryRequest request, string key, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class InspectionPreview : ITaskPlanPreviewClient
{
    public Task<TaskPlan> PlanChangeAsync(TaskPlanIntent intent, string expectedHost, CancellationToken ct) => throw new NotSupportedException();
    public Task<TaskPlan> PreviewAsync(TaskPlanIntent intent, string expectedHost, CancellationToken ct)
    {
        var steps = intent.Steps.Select(step => new TaskStep(step.StepId, step.ActionId, step.SchemaVersion,
            step.ImplementationRevision, step.Inputs, step.DependsOn, [], [], [], TaskStepDisposition.Conflict,
            ["runtime.operation_pending"], "read-only", "none", "none")).ToImmutableArray();
        var semantics = new TaskPlanSemantics(expectedHost, "rendering", intent.SourceKind, intent.SourceIdentity,
            intent.InputRevision, steps, "stop-on-failure", "safe-boundary", false, TaskPlanLimits.Default);
        return Task.FromResult(new TaskPlan(Guid.NewGuid(), DateTimeOffset.UtcNow, TaskPlanCanonicalizer.Digest(semantics), semantics));
    }
}

public class DisplayOnlyRuntime : DispatchProxy
{
    public bool Active;
    private readonly Guid _operation = Guid.NewGuid();
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "get_RuntimeId" => "sing-box",
        "get_SupportsRecoveryStopAndClose" => true,
        "InstallationAsync" when Active => Task.FromResult(new RuntimeInstallationSnapshot(
            new RuntimeInstallation(Guid.NewGuid(), "host-a", "sing-box", 1, RuntimeOwnership.Managed,
                new RuntimeTarget("linux", "x64", null, null, RuntimeInstallMode.Archive, "systemd"), "release", false),
            "1.14.3", null, null, _operation, InstallationStage.Downloading, null)),
        "OperationAsync" when Active => Task.FromResult<InstallationOperationDto?>(new(_operation,
            InstallationServiceId.SingBox, InstallationOperationKind.Upgrade, InstallationOperationState.Running,
            InstallationStage.Downloading, 42, null, DateTimeOffset.UtcNow, null, null, true)),
        "SourcesAsync" when Active => Task.FromResult<IReadOnlyList<RuntimeSourceView>>([]),
        _ => throw new InvalidOperationException("Rendering must not call a remote runtime: " + method?.Name)
    };
}
