using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
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
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                model.Navigate(ComponentCenterPage.Updates);
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Check(!model.HasResults && model.EmptyMessage.Resolve() == RelaxKonOS.Client.Localization.LocalizedText.Get("component_center.observation_unknown"),
                    "Updates with unobserved installation never claim there are no updates in " + language);
                using (var updateFrame = new RenderTargetBitmap(new PixelSize(1060, 800)))
                {
                    updateFrame.Render(window);
                    updateFrame.Save(Path.Combine(output, language + (dark ? "-dark" : "-light") + "-updates-unknown.png"), new PngBitmapEncoderOptions());
                }
                model.Navigate(ComponentCenterPage.All);
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var groupCheckbox = view.GetVisualDescendants().OfType<CheckBox>()
                    .Single(control => control.DataContext is ComponentTreeNode { IsGroup: true } group
                        && group.Children.Any(child => child.ComponentId == "sing-box"));
                groupCheckbox.Focus();
                window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
                window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
                Dispatcher.UIThread.RunJobs();
                Check(model.Drafts.Count == 0, "Space on a checked group removes local selections without a host change");
                model.Stage(new("sing-box", "sing-box", "host-a", Guid.NewGuid(), "source-a", "release-a", "1.14.3", RuntimeChange.Repair, true, false));
                var groupNode = (ComponentTreeNode)groupCheckbox.DataContext!;
                var displayOnlyChild = new ComponentTreeNode("display-only", RelaxKonOS.Client.Localization.LocalizedStatus.Literal("optional"), "display-only");
                groupNode.Children.Add(displayOnlyChild);
                groupNode.SynchronizeSelection(new HashSet<string> { "sing-box" });
                Dispatcher.UIThread.RunJobs();
                Check(groupCheckbox.IsChecked is null && groupCheckbox.Classes.Contains(":indeterminate") && model.Drafts.Count == 1,
                    "Partial aggregation still renders as indeterminate without creating another draft");
                groupNode.Children.Remove(displayOnlyChild);
                groupNode.SynchronizeSelection(new HashSet<string> { "sing-box" });
                var treeGroup = view.GetVisualDescendants().OfType<TreeViewItem>().Single(item => item.DataContext == groupNode);
                treeGroup.Focus();
                window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
                window.KeyRelease(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
                Dispatcher.UIThread.RunJobs();
                Check(!treeGroup.IsExpanded && model.Drafts.Count == 1, "Left arrow collapses the tree without changing selection intent");
                window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
                window.KeyRelease(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
                Dispatcher.UIThread.RunJobs();
                Check(treeGroup.IsExpanded && model.Drafts.Count == 1, "Right arrow expands the tree without executing or changing a draft");
                foreach (var page in Enum.GetValues<ComponentCenterPage>())
                {
                    model.Navigate(page); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                    Check(model.Drafts.Count == 1, "Page navigation keeps local intent");
                    using var bitmap = new RenderTargetBitmap(new PixelSize(1060, 800));
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(output, language + (dark ? "-dark-" : "-light-") + page + ".png"), new PngBitmapEncoderOptions());
                }
                // The catalog now carries the whole host component set; only runtime-backed entries
                // explain themselves with a host page, the rest point back at their owning application.
                var catalogNodes = model.VisibleRoots.SelectMany(root => root.DescendantsAndSelf())
                    .Where(node => node.ComponentId is not null).ToArray();
                Check(catalogNodes.Any(node => node.ComponentId == "mihomo") && catalogNodes.Any(node => node.ComponentId == "nginx")
                    && catalogNodes.Any(node => node.ComponentId == "samba"), "Catalog lists every declared host component in " + language);
                var singBoxNode = catalogNodes.Single(node => node.ComponentId == "sing-box");
                var mihomoNode = catalogNodes.Single(node => node.ComponentId == "mihomo");
                var nginxNode = catalogNodes.Single(node => node.ComponentId == "nginx");
                Check(singBoxNode.CanSelect && !mihomoNode.CanSelect && !nginxNode.CanSelect,
                    "Only the batch-integrated component may stage a draft from the tree");
                model.SelectedNode = mihomoNode;
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Check(model.ShowNotIntegrated && !model.HasRuntimeDetails && !model.SelectedUsesBatch,
                    "A component without a host page in this build never shows a lifecycle action");
                using (var bitmap = new RenderTargetBitmap(new PixelSize(1060, 800)))
                {
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(output, language + (dark ? "-dark-" : "-light") + "NotIntegrated.png"), new PngBitmapEncoderOptions());
                }
                model.SelectedNode = nginxNode;
                Dispatcher.UIThread.RunJobs();
                Check(model.SelectedDefinition?.Integration == ComponentIntegration.Application
                    && model.SelectedHostSources.Count + model.SelectedOtherSources.Count == 3
                    && model.SelectedHostSources.Concat(model.SelectedOtherSources).All(chip => !chip.Ready),
                    "A package-managed component declares its planned sources without claiming an adapter in " + language);
                Check(model.SelectedHostSources.Count + model.SelectedOtherSources.Count == 3
                    && model.SelectedOtherSources.All(chip => !chip.IsHostPlatform),
                    "Without a host platform answer every declared source is listed as another platform in " + language);
                model.SelectedNode = singBoxNode;
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var mihomoClient = DispatchProxy.Create<IRuntimeCatalogClient, DisplayOnlyRuntime>();
                ((DisplayOnlyRuntime)(object)mihomoClient).Runtime = "mihomo";
                var primaryClient = DispatchProxy.Create<IRuntimeCatalogClient, DisplayOnlyRuntime>();
                ((DisplayOnlyRuntime)(object)primaryClient).Active = true;
                using (var primaryRuntime = new ProxyServerRuntimeViewModel(primaryClient) { CanRead = true, CanManage = true })
                using (var mihomoRuntime = new ProxyServerRuntimeViewModel(mihomoClient))
                using (var multiModel = new ComponentCenterViewModel(primaryRuntime, new(), "https://host.example/", Guid.NewGuid(),
                    additionalRuntimes: new Dictionary<string, ProxyServerRuntimeViewModel> { ["mihomo"] = mihomoRuntime },
                    hostObservers: [new LayoutHostObserver()], lifecycle: new LayoutLifecycleClient()))
                {
                    primaryRuntime.LoadAsync().GetAwaiter().GetResult();
                    multiModel.SelectedNode = multiModel.VisibleRoots.SelectMany(root => root.DescendantsAndSelf())
                        .Single(node => node.ComponentId == "mihomo");
                    Check(ReferenceEquals(multiModel.DetailRuntime, mihomoRuntime) && multiModel.HasRuntimeDetails && !multiModel.SelectedUsesBatch,
                        "Selecting a runtime-backed component switches the detail pane to its own runtime");
                    multiModel.SelectedNode = multiModel.VisibleRoots.SelectMany(root => root.DescendantsAndSelf())
                        .Single(node => node.ComponentId == "sing-box");
                    Check(ReferenceEquals(multiModel.DetailRuntime, primaryRuntime), "Switching back restores the batch runtime");
                    // Nginx mixes a Linux package source with a Windows managed archive, so the host
                    // platform decides which declarations are shown as applicable.
                    Check(multiModel.HostPlatform == "linux", "The host platform comes from the Host's own target answer");
                    multiModel.SelectedNode = multiModel.VisibleRoots.SelectMany(root => root.DescendantsAndSelf())
                        .Single(node => node.ComponentId == "nginx");
                    Check(multiModel.SelectedHostSources.Count == 2 && multiModel.SelectedOtherSources.Count == 1
                        && multiModel.SelectedHostSources.All(chip => chip.Platform.Resolve() == "Linux")
                        && multiModel.SelectedOtherSources.All(chip => chip.Platform.Resolve() == "Windows"),
                        "Only the current platform's sources are presented as applicable");
                    Check(multiModel.ShowHostObservation && multiModel.CanObserveHost,
                        "A component with a read-only observer offers the host-facts panel");
                    multiModel.ObserveHostCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                    Check(multiModel.HasHostObservation
                        && multiModel.HostObservationVersionText.Resolve().Contains("1.24.0", StringComparison.Ordinal)
                        && !multiModel.HostObservationManagementText.Resolve().StartsWith("component_center.", StringComparison.Ordinal),
                        "Reading the owning application's facts never claims a component-center install");
                    var observationView = new ComponentCenterWorkspace(multiModel);
                    var observationWindow = new Window { Width = 1060, Height = 800, Content = observationView };
                    observationWindow.Show(); Dispatcher.UIThread.RunJobs(); observationWindow.UpdateLayout();
                    Check(!observationView.GetVisualDescendants().OfType<TextBlock>().Any(text =>
                        text.Text?.Contains("component_center.", StringComparison.Ordinal) == true),
                        "Host facts resolve in " + language);
                    using (var bitmap = new RenderTargetBitmap(new PixelSize(1060, 800)))
                    {
                        bitmap.Render(observationWindow);
                        bitmap.Save(Path.Combine(output, language + (dark ? "-dark-" : "-light") + "HostObservation.png"), new PngBitmapEncoderOptions());
                    }
                    // The package panel names the host's own candidate and the reason it cannot run yet.
                    Check(multiModel.ShowPackageFreeze && multiModel.CanResolvePackage,
                        "A component installed by the host offers the source-freeze panel");
                    multiModel.ResolvePackageCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                    Dispatcher.UIThread.RunJobs(); observationWindow.UpdateLayout();
                    Check(multiModel.HasPackageFreeze
                        && multiModel.PackageCandidateText.Resolve().Contains("1.26.3-3+deb13u1", StringComparison.Ordinal)
                        && multiModel.PackageRepositoryText.Resolve().Contains("trixie/main", StringComparison.Ordinal)
                        && multiModel.PackageVerdictText.Resolve().Contains("component_center.", StringComparison.Ordinal) == false
                        && !multiModel.PackageVerdictText.Resolve().StartsWith("component_center.", StringComparison.Ordinal)
                        && multiModel.PackageVerdictText.Resolve().Length > 0,
                        "The frozen candidate and its refusal resolve in " + language);
                    using (var bitmap = new RenderTargetBitmap(new PixelSize(1060, 800)))
                    {
                        observationView.FindControl<ScrollViewer>("DetailsScroll")!.Offset = new Vector(0, 420);
                        Dispatcher.UIThread.RunJobs(); observationWindow.UpdateLayout();
                        bitmap.Render(observationWindow);
                        bitmap.Save(Path.Combine(output, language + (dark ? "-dark-" : "-light") + "PackageFreeze.png"), new PngBitmapEncoderOptions());
                    }
                    observationWindow.Close(); Dispatcher.UIThread.RunJobs();
                }
                window.Width = 640; window.Height = 720;
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var navigationToggle = view.FindControl<Avalonia.Controls.Primitives.ToggleButton>("NavigationToggle")!;
                var navigationPane = view.FindControl<Border>("NavigationPane")!;
                var details = view.FindControl<ScrollViewer>("DetailsScroll")!;
                Check(navigationToggle.IsVisible && !navigationPane.IsVisible && details.IsVisible && details.Bounds.Width >= 600,
                    "Narrow layout dedicates the body width to details and keeps navigation reachable");
                navigationToggle.Focus();
                window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
                window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Check(navigationPane.IsVisible && !details.IsVisible && view.FindControl<TextBox>("Search")!.IsEffectivelyVisible,
                    "Keyboard opens the full-width narrow navigation without changing a draft");
                using (var bitmap = new RenderTargetBitmap(new PixelSize(640, 720)))
                {
                    bitmap.Render(window); bitmap.Save(Path.Combine(output, language + (dark ? "-dark-" : "-light-") + "NarrowNavigation.png"), new PngBitmapEncoderOptions());
                }
                search.Focus();
                model.Navigate(ComponentCenterPage.All);
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var previewChanges = view.FindControl<Button>("PreviewChanges")!;
                var previewOrigin = previewChanges.TranslatePoint(default, view)!.Value;
                Check(details.IsVisible && !navigationPane.IsVisible && model.Drafts.Count == 1 && details.Bounds.Height >= 200
                    && navigationToggle.IsKeyboardFocusWithin
                    && previewOrigin.X >= 0 && previewOrigin.X + previewChanges.Bounds.Width <= view.Bounds.Width
                    && previewOrigin.Y + previewChanges.Bounds.Height <= view.Bounds.Height,
                    "Narrow page selection returns to usable details with visible preview and unchanged local intent");
                using (var bitmap = new RenderTargetBitmap(new PixelSize(640, 720)))
                {
                    bitmap.Render(window); bitmap.Save(Path.Combine(output, language + (dark ? "-dark-" : "-light-") + "NarrowDetails.png"), new PngBitmapEncoderOptions());
                }
                window.Height = 540;
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var footerScroll = view.FindControl<ScrollViewer>("DraftFooterScroll")!;
                var discardChanges = view.FindControl<Button>("DiscardChanges")!;
                discardChanges.Focus(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var discardOrigin = discardChanges.TranslatePoint(default, view)!.Value;
                Check(footerScroll.Bounds.Height <= window.Height * .3 + 1 && details.Bounds.Height >= 140
                    && discardOrigin.Y >= 0 && discardOrigin.Y + discardChanges.Bounds.Height <= view.Bounds.Height,
                    "Short windows retain body space and keyboard can reach the scrolling footer action");
                navigationToggle.Focus();
                window.Width = 1060; window.Height = 800;
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Check(!navigationToggle.IsVisible && navigationPane.IsVisible && details.IsVisible && model.SelectedPage == ComponentCenterPage.All
                    && search.IsKeyboardFocusWithin && model.Drafts.Count == 1, "Widening restores the split view and moves focus out of the hidden toggle without losing state");
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
                changeView.FindControl<ScrollViewer>("DetailsScroll")!.Offset = new Vector(0, 420);
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
                var recoveryRun = taskModel.CurrentTaskRun;
                taskModel.TaskRecovery = new(recoveryRun, new(true, recoveryRun.Steps[0].DomainOperationId, RuntimeChange.Uninstall,
                    RelaxKonOS.Protocol.Installations.InstallationStage.Failed, true, "runtime.failed", "runtime.compensation_failed",
                    "sing-box-1.14.3", "sing-box-1.13.21", new(true, false, false)),
                    [new(Guid.NewGuid(), recoveryRun.RunId, recoveryRun.Steps[0].StepId, recoveryRun.Steps[0].DomainOperationId!.Value,
                        recoveryRun.Revision, TaskRecoveryAction.EmergencyStop, recoveryRun.CanonicalSubject, "recovery-session", recoveryRun.HostId,
                        "policy", "linux:0", "fixture", recoveryRun.CreatedAt.AddSeconds(1), recoveryRun.CreatedAt.AddMinutes(1), false, null)]);
                var recoveryView = new ComponentCenterWorkspace(taskModel); var recoveryWindow = new Window { Width = 1060, Height = 800, Content = recoveryView };
                recoveryWindow.Show(); Dispatcher.UIThread.RunJobs(); recoveryWindow.UpdateLayout();
                Check(taskModel.CanRecheckTaskRecovery && !taskModel.ShowTaskDomainProgress && !recoveryView.GetVisualDescendants().OfType<TextBlock>().Any(text =>
                    text.Text?.Contains("component_center.", StringComparison.Ordinal) == true), "Recovery recheck and guidance resolve in " + language);
                using (var bitmap = new RenderTargetBitmap(new PixelSize(1060, 800)))
                {
                    bitmap.Render(recoveryWindow); bitmap.Save(Path.Combine(output, language + (dark ? "-dark-" : "-light-") + "TaskRecovery.png"), new PngBitmapEncoderOptions());
                }
                recoveryWindow.Close(); Dispatcher.UIThread.RunJobs();
                foreach (var resultState in new[] { TaskRunState.Succeeded, TaskRunState.Failed, TaskRunState.Cancelled })
                {
                    taskModel.TaskNotice = RelaxKonOS.Client.Localization.LocalizedStatus.Literal("");
                    var stepState = resultState == TaskRunState.Succeeded ? TaskStepRunState.Succeeded
                        : resultState == TaskRunState.Failed ? TaskStepRunState.Failed : TaskStepRunState.Cancelled;
                    taskModel.CurrentTaskRun = recoveryRun with { State = resultState,
                        Steps = [recoveryRun.Steps[0] with { State = stepState, ProblemCode = resultState == TaskRunState.Failed ? "private-server-diagnostic" : null }] };
                    taskModel.TaskEvidence.Add(new(new(recoveryRun.Steps[0].StepId, recoveryRun.Steps[0].DomainOperationId!.Value,
                        InstallationOperationState.Interrupted, false, null)));
                    taskModel.TaskEvidence.Add(new(new(recoveryRun.Steps[0].StepId, recoveryRun.Steps[0].DomainOperationId!.Value,
                        InstallationOperationState.Interrupted, true, activeRuntime.Installation with
                        { Receipt = new(recoveryRun.Steps[0].DomainOperationId!.Value, RuntimeSwitchOutcome.Succeeded, null) })));
                    var resultView = new ComponentCenterWorkspace(taskModel);
                    var resultWindow = new Window { Width = 1060, Height = 800, Content = resultView };
                    resultWindow.Show(); Dispatcher.UIThread.RunJobs(); resultWindow.UpdateLayout();
                    Check(taskModel.ShowTaskResult && !resultView.GetVisualDescendants().OfType<TextBlock>().Any(text =>
                        text.Text?.Contains("component_center.", StringComparison.Ordinal) == true
                        || text.Text?.Contains("private-server-diagnostic", StringComparison.Ordinal) == true),
                        "Terminal result and safe step evidence resolve in " + language);
                    using (var bitmap = new RenderTargetBitmap(new PixelSize(1060, 800)))
                    {
                        bitmap.Render(resultWindow); bitmap.Save(Path.Combine(output, language + (dark ? "-dark-" : "-light-") + "TaskResult-" + resultState + ".png"), new PngBitmapEncoderOptions());
                    }
                    if (resultState == TaskRunState.Succeeded)
                    {
                        var operation = recoveryRun.Steps[0].DomainOperationId!.Value;
                        var committed = activeRuntime.Installation! with { ActiveRevisionId = operation, ConfigurationIdentity = new string('d', 64),
                            Receipt = new(operation, RuntimeSwitchOutcome.Succeeded, null) };
                        committed = committed with { CommitWitness = new(operation, committed.Installation, committed.ActiveRevisionId,
                            committed.ConfigurationIdentity, committed.MeasuredVersion) };
                        taskModel.TaskEvidence.Add(new(new(recoveryRun.Steps[0].StepId, operation, InstallationOperationState.Succeeded, true, committed)));
                        resultView.FindControl<ScrollViewer>("DetailsScroll")!.Offset = new Vector(0, 520);
                        Dispatcher.UIThread.RunJobs(); resultWindow.UpdateLayout();
                        using var bitmap = new RenderTargetBitmap(new PixelSize(1060, 800));
                        bitmap.Render(resultWindow); bitmap.Save(Path.Combine(output, language + (dark ? "-dark-" : "-light-") + "TaskEvidence.png"), new PngBitmapEncoderOptions());
                    }
                    resultWindow.Close(); Dispatcher.UIThread.RunJobs();
                }
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
    public Task<TaskRunEvidence> ReadEvidenceAsync(TaskPlan plan, TaskRun run, CancellationToken ct) => throw new NotSupportedException();
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

/// <summary>Read-only fixture: an Nginx instance the Web Server application already owns.</summary>
internal sealed class LayoutHostObserver : IComponentHostObserver
{
    public bool CanObserve(string componentId) => componentId == "nginx";
    public Task<ComponentHostObservation?> ObserveAsync(string componentId, string? hostOs, CancellationToken cancellationToken) =>
        Task.FromResult<ComponentHostObservation?>(new ComponentHostObservation(componentId,
            RelaxKonOS.Client.Localization.LocalizedStatus.Key("component_center.platform.linux", "Linux"),
            RelaxKonOS.Client.Localization.LocalizedStatus.Key("component_center.management.Integrated"),
            RelaxKonOS.Client.Localization.LocalizedStatus.Literal("1.24.0"),
            RelaxKonOS.Client.Localization.LocalizedStatus.Key("component_center.web_server_state.Running"),
            RelaxKonOS.Client.Localization.LocalizedStatus.Key("component_center.observation.managed_note"), true));
}

/// <summary>
/// Fixture for a host package: apt resolved one candidate and nothing can install it yet. It proves
/// the panel shows the exact frozen identity instead of a generic "planned" label.
/// </summary>
internal sealed class LayoutLifecycleClient : IComponentLifecycleClient
{
    private static readonly RelaxKonOS.Protocol.ManagedRuntimes.ComponentPackageTarget Package = new("nginx",
        "1.26.3-3+deb13u1", "amd64", "http://deb.debian.org/debian|trixie|main|amd64", "http://deb.debian.org/debian",
        "trixie", "main", "http://deb.debian.org/debian/pool/main/n/nginx/nginx_1.26.3-3+deb13u1_amd64.deb",
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", 540432, ["nginx-common_1.26.3-3+deb13u1_all.deb"]);

    public Task<RelaxKonOS.Protocol.ManagedRuntimes.ComponentStateView> StateAsync(string componentId, CancellationToken ct) =>
        Task.FromResult(new RelaxKonOS.Protocol.ManagedRuntimes.ComponentStateView(componentId,
            new RelaxKonOS.Protocol.ManagedRuntimes.ComponentState(componentId, "host-1", 42,
                RelaxKonOS.Protocol.ManagedRuntimes.RuntimeOwnership.Absent,
                RelaxKonOS.Protocol.ManagedRuntimes.ComponentSourceMode.SystemPackage,
                new RelaxKonOS.Protocol.ManagedRuntimes.RuntimeTarget("linux", "amd64", "trixie", null,
                    RelaxKonOS.Protocol.ManagedRuntimes.RuntimeInstallMode.SystemPackage, "systemd"), null, false),
            [new RelaxKonOS.Protocol.ManagedRuntimes.ComponentModeSupport(
                RelaxKonOS.Protocol.ManagedRuntimes.ComponentSourceMode.SystemPackage, "linux", true, null)], []));

    public Task<RelaxKonOS.Protocol.ManagedRuntimes.ComponentPlanResult> PlanAsync(
        RelaxKonOS.Protocol.ManagedRuntimes.ComponentPlanRequest request, CancellationToken ct) =>
        Task.FromResult(new RelaxKonOS.Protocol.ManagedRuntimes.ComponentPlanResult(request, null,
            RelaxKonOS.Protocol.ManagedRuntimes.ComponentFrozenTarget.FromPackage(Package),
            [RelaxKonOS.Protocol.ManagedRuntimes.ComponentProblemCodes.ExecutionUnavailable], []));
}

internal sealed class InspectionPreview : ITaskPlanPreviewClient{    public Task<TaskPlan> PlanChangeAsync(TaskPlanIntent intent, string expectedHost, CancellationToken ct) => throw new NotSupportedException();
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
    /// <summary>Selects which component identity the rendering fixture reports.</summary>
    public string Runtime = "sing-box";
    private readonly Guid _operation = Guid.NewGuid();
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "get_RuntimeId" => Runtime,
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
