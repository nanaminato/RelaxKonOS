using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.ApplicationDeployments;
using RelaxKonOS.Client.Apps.ApplicationDeployments.Views;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.WindowManager;
using AppContext = System.AppContext;

internal static class ApplicationDeploymentsWorkspaceChecks
{
    public static void Run(ShellSettings settings)
    {
        var output = Path.Combine(AppContext.BaseDirectory, "preview-qa", "application-deployments");
        Directory.CreateDirectory(output);
        var languageBefore = settings.Language;
        var appearanceBefore = settings.Appearance;
        try
        {
            foreach (var language in new[] { "zh-CN", "en-US", "ja-JP" })
            {
                settings.Language = language;
                Dispatcher.UIThread.RunJobs();
                Wide(language, output);
            }
            Narrow(settings, output);
            EmptyState(settings, output);
            CatalogReceipt();
            ManagedCatalogForm();
            CatalogUpgradeOptions();
            RetainedActions();
            BusyState(settings, output);
            RefusedState(settings, output);
            DarkAppearance(settings, output);
        }
        finally
        {
            settings.Language = languageBefore;
            settings.Appearance = appearanceBefore;
            Dispatcher.UIThread.RunJobs();
        }
        Console.WriteLine("Application deployment workspace checks passed (" + output + ").");
    }

    private static void ManagedCatalogForm()
    {
        var client = DispatchProxy.Create<IRemoteApplicationDeploymentClient, DeploymentHostStub>();
        var host = (DeploymentHostStub)(object)client;
        host.Seed();
        var requirements = new[] { ServerCapabilities.ApplicationDeployments, ServerCapabilities.Docker, ServerCapabilities.ApplicationDeploymentsCompose };
        var release = new ApplicationCatalogTemplateDto("2", "added-from-catalog", "1.2.3", "RelaxKonOS", "built-in", true,
            "Managed test release", "A release supplied after the client was built.", ["linux/amd64"], requirements, new(),
            [new("databaseToken", "secret", true, null, [], new("Token", "令牌", "トークン"))], [], 8080, "/healthz", "Maintenance");
        var descriptor = new ServerDescriptorDto(HostPlatformKind.Linux, requirements);
        Check(!ApplicationCatalogView.CanInstall(release, descriptor with { Capabilities = requirements[..2] })
            && !ApplicationCatalogView.CanInstall(release, descriptor with { Platform = HostPlatformKind.Windows })
            && ApplicationCatalogView.CanInstall(release, descriptor), "The same desktop client gates a multi-service release by authenticated Server capabilities.");
        var application = host.Applications[0];
        host.InstallReceipt = new(application, host.Snapshots[application.Id].Operations[0], release.Id, release.Version);
        host.Topology = new(release.Id, release.Version, "127.0.0.1", 18083, "web", "/healthz",
            [new("web", "image", 8080, ["db"], []), new("db", "image", 5432, [], [new("db-data", "/db-data")])]);
        var manager = new RelaxKonOS.WindowManager.WindowManager();
        manager.Attach(new Canvas { Width = 1000, Height = 800 });
        var owner = manager.Create(new WindowCreateOptions(new("test.catalog"), "Owner", new TextBlock(),
            new RelaxKonOS.Core.Primitives.Rect(0, 0, 800, 600)));
        ApplicationCatalogView? view = null;
        ModalDialog<CatalogApplicationInstallDto?>? handle = null;
        _ = manager.ShowDialogAsync<CatalogApplicationInstallDto?>(owner, "Catalog", dialog =>
        {
            handle = dialog;
            return view = new ApplicationCatalogView(client, [release], descriptor, dialog);
        });
        var form = (StackPanel)((ScrollViewer)view!.Content!).Content!;
        var port = form.Children.OfType<TextBox>().Single(text => text.Name == "CatalogHostPort");
        form.Children.OfType<TextBox>().Single(text => text.Name == "CatalogInstanceName").Text = "managed-test";
        var secret = form.Children.OfType<StackPanel>().First().Children.OfType<TextBox>().Single();
        secret.Text = "synthetic-password";
        var preview = form.Children.OfType<Button>().Single(button => button.Name == "CatalogPreviewTopology");
        var install = form.Children.OfType<StackPanel>().Last().Children.OfType<Button>().Single(button => button.Name == "CatalogInstall");
        var topology = form.Children.OfType<TextBlock>().Single(text => text.Name == "CatalogTopology");
        port.Text = "18083";
        Dispatcher.UIThread.RunJobs();
        Check(secret.PasswordChar != '\0' && preview.IsVisible && !install.IsEnabled, "The package field is masked and installation waits for a topology preview.");
        preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(host.PreviewRequest == (release.Id, release.Version, 18083) && install.IsEnabled
            && topology.Text?.Contains("127.0.0.1:18083", StringComparison.Ordinal) == true
            && topology.Text.Contains("/db-data", StringComparison.Ordinal),
            "The preview binds the exact release and port and displays the internal database volume.");
        port.Text = "18084";
        Dispatcher.UIThread.RunJobs();
        Check(!install.IsEnabled && string.IsNullOrEmpty(topology.Text), "Changing the requested port invalidates the previous approval.");
        port.Text = "18083";
        Dispatcher.UIThread.RunJobs();
        preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(handle!.Result.IsCompletedSuccessfully && host.InstallRequest is { Confirmed: true, HostPort: 18083,
            Fields: [{ Id: "databaseToken", Value: "synthetic-password" }] }
            && host.InstallRequest.TemplateId == release.Id && host.InstallRequest.TemplateVersion == release.Version,
            "The desktop submits the approved release with the server-defined field and receives the original install receipt.");
    }

    private static void CatalogReceipt()
    {
        var model = Model(out var host);
        var view = ApplicationDeploymentsWorkspace.Create(model);
        var application = host.Applications[1];
        var operation = host.Snapshots[host.Applications[0].Id].Operations[0] with { ApplicationId = application.Id };
        var release = new ApplicationCatalogTemplateDto("2", "added-by-data", "4.3.2", "RelaxKonOS", "built-in", true,
            "New package", "A release returned by the test host.", ["linux/amd64"], [], new(),
            [new("serviceToken", "secret", true, null, [], new("Token", "令牌", "トークン"))],
            [], 8080, "/healthz", "Maintenance");
        host.Catalog = [release];
        model.ShowCatalogAsync = releases =>
        {
            Check(releases.Count == 1 && releases[0] == release, "Catalog command forwards arbitrary release and field metadata to the dialog.");
            return Task.FromResult<CatalogApplicationInstallDto?>(new(application, operation, release.Id, release.Version));
        };
        Dispatcher.UIThread.RunJobs();
        Check(view.FindControl<Button>("OpenCatalogButton")?.Command == model.OpenCatalogCommand, "Workspace binds its official catalog action.");
        var submitted = model.OpenCatalogCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Check(submitted.IsCompletedSuccessfully && model.SelectedApplication?.Id == application.Id,
            "Catalog install receipt selects its instance instead of the previous selection.");
    }

    private static void CatalogUpgradeOptions()
    {
        var model = Model(out var host);
        var application = host.Applications[0] with { CatalogTemplateId = "data-app", CatalogTemplateVersion = "1.0.0" };
        host.Applications[0] = application;
        host.Snapshots[application.Id] = host.Snapshots[application.Id] with { Application = application };
        var candidate = new ApplicationCatalogTemplateDto("2", "data-app", "2.0.0", "RelaxKonOS", "built-in", true,
            "New version", "Target release", ["linux/amd64"], [], new(), [], [], 8080, "/healthz", "Maintenance");
        host.Catalog = [candidate, candidate with { Version = "1.0.0" }, candidate with { Id = "other-app" },
            candidate with { Version = "3.0.0", Withdrawn = true }];
        model.StartAsync().GetAwaiter().GetResult();
        Check(model.ShowCatalogUpgradeAction && !model.ShowManualDeployAction, "Catalog instances expose the catalog upgrade entry point.");
        var showedPreview = false;
        model.ShowCatalogUpgradeAsync = (current, releases) =>
        {
            Check(current.Id == application.Id && releases.SequenceEqual([candidate]),
                "Upgrade picker excludes the installed release, other packages and withdrawn releases.");
            showedPreview = true;
            return Task.FromResult<DeploymentOperationDto?>(null);
        };
        model.UpgradeCatalogCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(showedPreview, "The catalog upgrade command loads targets from the server.");
    }

    private static void RetainedActions()
    {
        var model = Model(out var host);
        var application = host.Applications[0] with
        {
            DesiredState = ApplicationDesiredState.Retained,
            ActualState = ApplicationActualState.Stopped,
            CatalogTemplateId = "added-by-data",
            Endpoint = null,
        };
        host.Applications[0] = application;
        host.Snapshots[application.Id] = host.Snapshots[application.Id] with { Application = application };
        model.StartAsync().GetAwaiter().GetResult();
        Check(model.ShowReinstallAction && !model.ShowStartAction && !model.CanActOnSelection && !model.HasEndpointAddress,
            "A retained catalog instance offers reinstall without advertising a live endpoint or ordinary lifecycle actions.");
    }

    /// <summary>The two-pane workspace: the list, the facts, and the actions that apply to the observed state.</summary>
    private static void Wide(string language, string output)
    {
        var model = Model(out _);
        var view = ApplicationDeploymentsWorkspace.Create(model);
        var window = new Window { Width = 1220, Height = 800, Content = view };
        try
        {
            window.Show();
            Pump(window);

            Check(model.Applications.Count == 2 && model.HasSelection,
                language + ": the list loads every application and selects one.");
            Check(model.EndpointAddress == "127.0.0.1:18080" && model.CanOpenEndpoint && model.IsReadinessHealthy,
                language + ": a running HTTP workload reads back a dialable address and a healthy readiness verdict.");
            Check(model.ShowStopAction && model.ShowRestartAction && !model.ShowStartAction,
                language + ": a running instance offers stop and restart, not start.");
            Check(view.FindControl<StackPanel>("EmptyDetailPanel")!.IsVisible == false,
                language + ": the detail page replaces the empty notice as soon as an application is selected.");
            Capture(window, Path.Combine(output, language + "-overview.png"), 1220, 800);

            // Rollback is reached from the revision it returns to.
            Check(model.ShowRollbackHint && !model.CanRollback,
                language + ": rollback is unavailable before a revision is chosen.");
            model.SelectedRevision = model.Revisions.First(revision => !revision.IsCurrent);
            Dispatcher.UIThread.RunJobs();
            Check(model.CanRollback && model.RollbackActionText.Contains(model.SelectedRevision.NumberText, StringComparison.Ordinal),
                language + ": selecting an older revision names the rollback target.");
            model.SelectedRevision = model.Revisions.First(revision => revision.IsCurrent);
            Dispatcher.UIThread.RunJobs();
            Check(!model.CanRollback && model.SelectedRevisionIsCurrent,
                language + ": the current revision reads as the running one instead of as a disabled rollback.");

            Check(model.Operations.Count == 2 && model.Operations[0].StateSucceeded && model.Operations[1].StateFailed
                && !model.Operations[0].StateFailed,
                language + ": operation rows carry the tone of the state the server recorded.");
            CheckNoLeakedKeys(view, language);

            view.FindControl<TabControl>("DetailTabs")!.SelectedIndex = 0;
            Pump(window);
            Capture(window, Path.Combine(output, language + "-revisions.png"), 1220, 800);
            view.FindControl<TabControl>("DetailTabs")!.SelectedIndex = 1;
            Pump(window);
            Capture(window, Path.Combine(output, language + "-operations.png"), 1220, 800);

            // A stopped instance swaps the lifecycle pair.
            model.SelectedApplication = model.Applications[1];
            Dispatcher.UIThread.RunJobs();
            Check(model.ShowStartAction && !model.ShowStopAction,
                language + ": a stopped instance offers start instead of stop and restart.");
            Check(model.HasEndpointAddress == false && model.CanOpenEndpoint == false,
                language + ": a workload without a published port offers no address and no browser action.");
        }
        finally { window.Close(); }
    }

    /// <summary>Below 900 DIP the list and the detail become two pages instead of two crushed columns.</summary>
    private static void Narrow(ShellSettings settings, string output)
    {
        var languageBefore = settings.Language;
        settings.Language = "zh-CN";
        Dispatcher.UIThread.RunJobs();
        try
        {
            var model = Model(out _);
            var view = ApplicationDeploymentsWorkspace.Create(model);
            var window = new Window { Width = 640, Height = 760, Content = view };
            try
            {
                window.Show();
                Pump(window);
                var list = view.FindControl<Border>("ListPane")!;
                var detail = view.FindControl<Grid>("DetailHost")!;
                var back = view.FindControl<Button>("BackToListButton")!;
                Check(list.IsVisible && !detail.IsVisible,
                    "A narrow window opens on the application list instead of two squeezed columns.");
                Capture(window, Path.Combine(output, "narrow-list.png"), 640, 760);

                model.SelectedApplication = model.Applications[1];
                Pump(window);
                Check(detail.IsVisible && !list.IsVisible && back.IsVisible,
                    "Selecting an application moves a narrow window to its detail page and offers the way back.");
                Capture(window, Path.Combine(output, "narrow-detail.png"), 640, 760);

                back.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Pump(window);
                Check(list.IsVisible && !detail.IsVisible && model.HasSelection,
                    "The back action returns to the list without dropping the selection.");
            }
            finally { window.Close(); }
        }
        finally
        {
            settings.Language = languageBefore;
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>A server with no deployments must not look like a failed load.</summary>
    private static void EmptyState(ShellSettings settings, string output)
    {
        var languageBefore = settings.Language;
        settings.Language = "zh-CN";
        Dispatcher.UIThread.RunJobs();
        try
        {
            var model = Model(out _, empty: true);
            var view = ApplicationDeploymentsWorkspace.Create(model);
            var window = new Window { Width = 1220, Height = 800, Content = view };
            try
            {
                window.Show();
                Pump(window);
                Check(!model.HasApplications && view.FindControl<Border>("EmptyListPanel")!.IsVisible
                    && view.FindControl<StackPanel>("EmptyDetailPanel")!.IsVisible,
                    "An empty server explains the list and the detail page instead of showing two blank surfaces.");
                Capture(window, Path.Combine(output, "empty.png"), 1220, 800);
            }
            finally { window.Close(); }
        }
        finally
        {
            settings.Language = languageBefore;
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>While an operation is running, every write is withheld and cancel is the live action.</summary>
    private static void BusyState(ShellSettings settings, string output)
    {
        var languageBefore = settings.Language;
        settings.Language = "zh-CN";
        Dispatcher.UIThread.RunJobs();
        try
        {
            var model = Model(out _, busy: true);
            var view = ApplicationDeploymentsWorkspace.Create(model);
            var window = new Window { Width = 1220, Height = 800, Content = view };
            try
            {
                window.Show();
                Pump(window);
                Check(model.IsOperationRunning && model.HasActiveOperation,
                    "The workspace reattaches to a running operation instead of waiting for the request that queued it.");
                Check(!model.CanAct && !view.FindControl<Button>("NewDeploymentButton")!.IsEffectivelyEnabled,
                    "Starting a second deployment is withheld while one is running.");
                Check(model.CanCancelActiveOperation
                    && view.FindControl<Button>("CancelOperationButton")!.IsEffectivelyEnabled,
                    "Cancelling stays available while the operation is running and cancellable.");
                Check(view.FindControl<Border>("ActiveOperationBar")!.IsVisible,
                    "The running operation keeps its own bar with the stage and the log toggle.");
                Capture(window, Path.Combine(output, "busy.png"), 1220, 800);
            }
            finally { window.Close(); }
        }
        finally
        {
            settings.Language = languageBefore;
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>A refused read is stated in the page and the status bar, not only in a tooltip.</summary>
    private static void RefusedState(ShellSettings settings, string output)
    {
        var languageBefore = settings.Language;
        settings.Language = "zh-CN";
        Dispatcher.UIThread.RunJobs();
        try
        {
            var model = Model(out var stub);
            stub.FailSnapshots = true;
            model.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            var view = ApplicationDeploymentsWorkspace.Create(model);
            var window = new Window { Width = 1220, Height = 800, Content = view };
            try
            {
                window.Show();
                Pump(window);
                Check(model.HasError && model.Applications.Count == 2,
                    "A refused snapshot keeps the loaded list and states the failure.");
                Check(model.Revisions.Count == 2 && model.Operations.Count == 2,
                    "A refused refresh keeps the revisions and operations already on screen.");
                CheckNoLeakedKeys(view, "zh-CN refused");
                Capture(window, Path.Combine(output, "refused.png"), 1220, 800);
            }
            finally { window.Close(); }
        }
        finally
        {
            settings.Language = languageBefore;
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void DarkAppearance(ShellSettings settings, string output)
    {
        var languageBefore = settings.Language;
        var appearanceBefore = settings.Appearance;
        settings.Language = "zh-CN";
        settings.Appearance = settings.Appearance with { Mode = ThemeKind.Dark };
        Dispatcher.UIThread.RunJobs();
        try
        {
            var model = Model(out _);
            var view = ApplicationDeploymentsWorkspace.Create(model);
            var window = new Window { Width = 1220, Height = 800, Content = view };
            try
            {
                window.Show();
                Pump(window);
                CheckNoLeakedKeys(view, "zh-CN dark");
                Capture(window, Path.Combine(output, "dark.png"), 1220, 800);
            }
            finally { window.Close(); }
        }
        finally
        {
            settings.Language = languageBefore;
            settings.Appearance = appearanceBefore;
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static ApplicationDeploymentsViewModel Model(out DeploymentHostStub stub, bool empty = false, bool busy = false)
    {
        var client = DispatchProxy.Create<IRemoteApplicationDeploymentClient, DeploymentHostStub>();
        stub = (DeploymentHostStub)(object)client;
        stub.Seed(empty, busy);
        var model = new ApplicationDeploymentsViewModel(client, new DeploymentTestPermissions());
        model.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        return model;
    }

    private static void Capture(Window window, string path, int width, int height)
    {
        Pump(window);
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The window did not render a frame.");
        using var fallback = frame;
        using (var bitmap = new RenderTargetBitmap(new PixelSize(width, height)))
        {
            bitmap.Render(window);
            bitmap.Save(path);
        }
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static void CheckNoLeakedKeys(Control view, string label)
    {
        var leaked = view.GetVisualDescendants().OfType<TextBlock>()
            .Select(block => block.Text)
            .Where(text => text is not null
                && (text.Contains("application_deployments.", StringComparison.Ordinal)
                    || text.Contains("application-deployment.", StringComparison.Ordinal)
                    || text.Contains("[missing", StringComparison.Ordinal)))
            .ToList();
        Check(leaked.Count == 0, label + ": every visible label resolves"
            + (leaked.Count > 0 ? " (" + leaked[0] + ")" : ""));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}

/// <summary>A server-side fixture with two deployments, two revisions, one succeeded and one failed
/// operation, and an optional running operation for the busy state.</summary>
public class DeploymentHostStub : DispatchProxy
{
    public IReadOnlyList<ApplicationCatalogTemplateDto> Catalog { get; set; } = [];
    public List<ApplicationDto> Applications { get; } = [];
    public Dictionary<Guid, ApplicationDeploymentSnapshotDto> Snapshots { get; } = [];
    public DeploymentOperationDto? ActiveOperation { get; set; }
    public bool FailSnapshots { get; set; }
    public CatalogApplicationTopologyDto? Topology { get; set; }
    public CatalogApplicationInstallDto? InstallReceipt { get; set; }
    public (string Id, string Version, int Port)? PreviewRequest { get; private set; }
    public InstallCatalogApplicationRequest? InstallRequest { get; private set; }

    public void Seed(bool empty = false, bool busy = false)
    {
        Applications.Clear();
        Snapshots.Clear();
        if (empty) return;

        var shopId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var revision1 = Guid.NewGuid();
        var revision2 = Guid.NewGuid();
        var now = DateTimeOffset.Now;

        var shop = new ApplicationDto(shopId, "shop-api", "user:ops", ApplicationSourceKind.Image,
            ApplicationWorkloadKind.Web, ApplicationDesiredState.Running, ApplicationActualState.Running,
            ApplicationReadinessLevel.Http, "/healthz", 8080, 18080, "127.0.0.1",
            new ApplicationEndpointDto("http", 8080, 18080, "127.0.0.1"),
            new ApplicationResourceLimitsDto(1, 512L * 1024 * 1024, 128),
            [new ApplicationVolumeDto("shop-data", "/data")],
            [new ApplicationConfigEntryDto("ASPNETCORE_ENVIRONMENT", "Production")],
            revision2, 2, "relaxkonos-shop-api", "9f2c0a4d5e6b", null, "shop.example.com",
            now.AddDays(-9), now.AddMinutes(-4));
        var shopRevision2 = new ApplicationRevisionDto(revision2, shopId, 2, ApplicationSourceKind.Image, "2026.1",
            "registry.example.com/shop/api:2.4.0", "registry.example.com/shop/api:2.4.0", "sha256:9f2c0a4d5e6b7c8d9e0f",
            "linux/amd64", null, ApplicationWorkloadKind.Web, ApplicationReadinessLevel.Http, "/healthz",
            "dotnet", ["shop.dll"], 8080, 18080, "127.0.0.1", new ApplicationResourceLimitsDto(),
            [new ApplicationVolumeDto("shop-data", "/data")],
            [new ApplicationConfigEntryDto("ASPNETCORE_ENVIRONMENT", "Production")],
            null, true, "user:ops", now.AddMinutes(-30));
        var shopRevision1 = new ApplicationRevisionDto(revision1, shopId, 1, ApplicationSourceKind.Image, "2026.1",
            "registry.example.com/shop/api:2.3.1", "registry.example.com/shop/api:2.3.1", "sha256:1a2b3c4d5e6f7a8b9c0d",
            "linux/amd64", null, ApplicationWorkloadKind.Web, ApplicationReadinessLevel.Http, "/healthz",
            "dotnet", ["shop.dll"], 8080, 18080, "127.0.0.1", new ApplicationResourceLimitsDto(),
            [new ApplicationVolumeDto("shop-data", "/data")],
            [new ApplicationConfigEntryDto("ASPNETCORE_ENVIRONMENT", "Production")],
            null, false, "user:ops", now.AddDays(-3));

        var worker = new ApplicationDto(workerId, "worker-report", "user:ops", ApplicationSourceKind.PythonProject,
            ApplicationWorkloadKind.Worker, ApplicationDesiredState.Stopped, ApplicationActualState.Stopped,
            ApplicationReadinessLevel.Process, null, 9000, null, "127.0.0.1", null,
            new ApplicationResourceLimitsDto(), [], [], revision1, 1, "relaxkonos-worker-report", "71ae",
            null, null, now.AddDays(-2), now.AddHours(-3));
        var workerRevision = new ApplicationRevisionDto(revision1, workerId, 1, ApplicationSourceKind.PythonProject,
            "2026.1", "report-worker", "localhost/relaxkonos/worker-report:1", "sha256:7a8b9c0d1e2f3a4b5c6d",
            "linux/amd64", "python:3.12-slim", ApplicationWorkloadKind.Worker, ApplicationReadinessLevel.Process,
            null, "python", ["worker.py"], 9000, null, "127.0.0.1", new ApplicationResourceLimitsDto(),
            [], [], null, true, "user:ops", now.AddDays(-2));

        var succeeded = new DeploymentOperationDto(Guid.NewGuid(), shopId, "shop-api",
            DeploymentOperationKind.Deploy, revision2, 2, DeploymentOperationState.Succeeded,
            DeploymentStage.Completed, 100, null, null, "user:ops",
            now.AddMinutes(-35), now.AddMinutes(-35), now.AddMinutes(-31), false);
        var failed = new DeploymentOperationDto(Guid.NewGuid(), shopId, "shop-api",
            DeploymentOperationKind.Deploy, revision2, 2, DeploymentOperationState.Failed,
            DeploymentStage.HealthChecking, null, "application-deployment.health_check_timeout",
            "application-deployment.drift_container_missing", "user:ops",
            now.AddMinutes(-52), now.AddMinutes(-52), now.AddMinutes(-49), false);
        var running = new DeploymentOperationDto(Guid.NewGuid(), shopId, "shop-api",
            DeploymentOperationKind.Deploy, revision2, 2, DeploymentOperationState.Running,
            DeploymentStage.HealthChecking, 60, null, null, "user:ops",
            now.AddMinutes(-2), now.AddMinutes(-2), null, true);

        Applications.Add(shop);
        Applications.Add(worker);
        Snapshots[shopId] = new ApplicationDeploymentSnapshotDto(shop, [shopRevision2, shopRevision1],
            [succeeded, failed], busy ? running : null);
        Snapshots[workerId] = new ApplicationDeploymentSnapshotDto(worker, [workerRevision], []);
        ActiveOperation = busy ? running : null;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "ListTemplatesAsync" => Task.FromResult<IReadOnlyList<ApplicationDeploymentTemplateDto>>([]),
        "ListCatalogAsync" => Task.FromResult(Catalog),
        "PreviewCatalogTopologyAsync" => PreviewTopology((string)args![0]!, (string)args[1]!, (int)args[2]!),
        "InstallCatalogAsync" => Install((InstallCatalogApplicationRequest)args![0]!),
        "ListApplicationsAsync" => Task.FromResult<IReadOnlyList<ApplicationDto>>([.. Applications]),
        "GetSnapshotAsync" => Snapshot((Guid)args![0]!),
        "GetOperationAsync" => Task.FromResult(ActiveOperation),
        "GetLogsAsync" => Task.FromResult(new DeploymentLogDto((Guid)args![0]!, null, null,
            ["2026-11-02T12:00:00Z info: application started", "2026-11-02T12:00:01Z info: listening on :8080"], false)),
        "GetActiveOperationAsync" => Task.FromResult(ActiveOperation),
        _ => throw new InvalidOperationException(method.Name),
    };

    private Task<CatalogApplicationTopologyDto> PreviewTopology(string id, string version, int port)
    {
        PreviewRequest = (id, version, port);
        return Task.FromResult(Topology!);
    }

    private Task<CatalogApplicationInstallDto> Install(InstallCatalogApplicationRequest request)
    {
        InstallRequest = request;
        return Task.FromResult(InstallReceipt!);
    }

    private Task<ApplicationDeploymentSnapshotDto?> Snapshot(Guid applicationId)
    {
        if (FailSnapshots) throw new HttpRequestException("the host is unreachable");
        return Task.FromResult(Snapshots.GetValueOrDefault(applicationId));
    }
}

internal sealed class DeploymentTestPermissions : IAppPermissionScope
{
    public AppPermissionStatus GetStatus(string permissionId) => AppPermissionStatus.Granted;
    public bool IsGranted(string permissionId) => true;
    public Task<AppPermissionStatus> RequestAsync(string permissionId, CancellationToken cancellationToken = default)
        => Task.FromResult(AppPermissionStatus.Granted);
    public Task OpenSettingsAsync() => Task.CompletedTask;
}
