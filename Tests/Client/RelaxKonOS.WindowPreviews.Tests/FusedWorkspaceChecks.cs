using System.Reflection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Apps.Docker;
using RelaxKonOS.Client.Apps.Docker.Views;
using RelaxKonOS.Client.Apps.Proxy;
using RelaxKonOS.Client.Apps.Proxy.Views;
using RelaxKonOS.Client.Apps.Tunnels;
using RelaxKonOS.Client.Apps.Tunnels.Views;
using RelaxKonOS.Client.Apps.PortForwarding;
using RelaxKonOS.Client.Apps.PortForwarding.ViewModels;
using RelaxKonOS.Client.Apps.PortForwarding.Views;
using RelaxKonOS.Client.Apps.WebServers;
using RelaxKonOS.Client.Apps.WebServers.Views;
using RelaxKonOS.Client.Apps.Certificates;
using RelaxKonOS.Client.Apps.Certificates.Views;
using RelaxKonOS.Client.Apps.ApplicationDeployments;
using RelaxKonOS.Client.Apps.ApplicationDeployments.Views;
using RelaxKonOS.Client.Apps.FileServices;
using RelaxKonOS.Client.Apps.FileServices.Views;
using RelaxKonOS.Client.Apps.Notepad;
using RelaxKonOS.Client.Apps.ImageViewer.ViewModels;
using RelaxKonOS.Client.Apps.ImageViewer.Views;
using RelaxKonOS.Client.Apps.TaskManager;
using RelaxKonOS.Client.Apps.TaskManager.ViewModels;
using RelaxKonOS.Client.Apps.TaskManager.Views;
using RelaxKonOS.Client.Apps.Browser.ViewModels;
using RelaxKonOS.Client.Apps.Browser.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.ServerCenter.Views;
using RelaxKonOS.Client.Apps.Git;
using RelaxKonOS.Client.Apps.Git.Views;
using RelaxKonOS.Client.Controls;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.ViewModels.ServerCenter;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Protocol.Workspace.SystemStyles;
using RelaxKonOS.WindowManager;
using CoreRect = RelaxKonOS.Core.Primitives.Rect;

internal static class FusedWorkspaceChecks
{
    public static void Run(ShellSettings settings)
    {
        var original = settings.ToPreferences();
        var login = new LoginLocalizationService(new LocalLanguageStore());
        var server = new ServerCenterViewModel(null!, null!, null!, null!, new SshDesktopSession(null!), null!, null!, login);
        var git = new GitClientViewModel(null!) { IsPickerMode = false };
        var serverView = new ServerCenterWorkspace { DataContext = server };
        var gitView = GitClientWorkspace.Create(git);
        var auth = DispatchProxy.Create<IAuthSession, SiteEditorSession>();
        var permissions = DispatchProxy.Create<IAppPermissionScope, HeaderPreviewPermissions>();
        var proxyModel = new ProxyManagerViewModel(DispatchProxy.Create<IProxyRepository, HeaderPreviewPermissions>(), false, false);
        using var tunnelModel = new TunnelManagerViewModel(null!, false);
        using var forwardModel = new PortForwardingViewModel(new PortForwardingService(null!, new PortForwardingSettingsStore(Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "unused-forward-settings.json"))));
        var dockerModel = new DockerManagerViewModel(DispatchProxy.Create<IRemoteDockerClient, HeaderPreviewPermissions>());
        var certificateModel = new CertificateManagerViewModel(null!, auth, permissions);
        var webModel = new WebServerManagerViewModel(null!, null!, auth, permissions, null!);
        var deploymentModel = new ApplicationDeploymentsViewModel(null!, permissions);
        var filesModel = new FileServicesViewModel(null!, permissions, () => true);
        var notepadModel = new NotepadViewModel(null!, "UTF-8");
        using var imageModel = new ImageViewerViewModel(null!);
        var tasksModel = new TaskManagerViewModel(null!, new PerformanceStream(auth));
        var browserModel = new BrowserViewModel(DispatchProxy.Create<RelaxKonOS.Client.Apps.Browser.IBrowserClient, HeaderPreviewPermissions>());
        var browserView = new BrowserMainView { DataContext = browserModel };
        var canvas = new Canvas();
        var host = new Window { Width = 1100, Height = 800, Content = canvas };
        var manager = new RelaxKonOS.WindowManager.WindowManager();
        try
        {
            host.Show(); Pump(); manager.Attach(canvas); manager.SetHostBounds(new CoreRect(0, 0, 1100, 800));
            var serverWindow = manager.Create(new WindowCreateOptions(new AppId("test.server-center"), "Server center", serverView, new CoreRect(10, 10, 960, 640)));
            var gitWindow = manager.Create(new WindowCreateOptions(new AppId("test.git"), "Git", gitView, new CoreRect(10, 10, 960, 640)));
            serverView.AttachWindowHeader(serverWindow); gitView.AttachWindowHeader(gitWindow);
            var samples = new List<(ManagedWindow window, UserControl view, string name)> { (serverWindow, serverView, "server-center"), (gitWindow, gitView, "git") };
            Add(DockerManagerWorkspace.Create(dockerModel, new DockerProxyViewModel(null!), new DockerImageMirrorsViewModel(null!), Done, Done, Done, Done, Done), "docker", (view, window) => view.AttachWindowHeader(window));
            Add(new ProxyManagerWorkspace(proxyModel), "proxy", (view, window) => view.AttachWindowHeader(window));
            Add(new TunnelManagerView { DataContext = tunnelModel }, "tunnels", (view, window) => view.AttachWindowHeader(window));
            Add(new PortForwardingMainView { DataContext = forwardModel }, "port-forwarding", (view, window) => view.AttachWindowHeader(window));
            Add(WebServerManagerWorkspace.Create(webModel), "webservers", (view, window) => view.AttachWindowHeader(window));
            Add(CertificateManagerWorkspace.Create(certificateModel, Done, Done, () => { }), "certificates", (view, window) => view.AttachWindowHeader(window));
            Add(ApplicationDeploymentsWorkspace.Create(deploymentModel), "application-deployments", (view, window) => view.AttachWindowHeader(window));
            Add(new FileServicesWorkspace(filesModel), "file-services", (view, window) => view.AttachWindowHeader(window));
            Add(new NotepadView { DataContext = notepadModel }, "notepad", (view, window) => view.AttachWindowHeader(window));
            Add(new ImageViewerView { DataContext = imageModel }, "imageviewer", (view, window) => view.AttachWindowHeader(window));
            Add(new TaskManagerMainView { DataContext = tasksModel }, "taskmanager", (view, window) => view.AttachWindowHeader(window));
            Add(browserView, "browser", (view, window) => view.AttachWindowHeader(window));

            void Add<T>(T view, string name, Action<T, ManagedWindow> attach) where T : UserControl
            {
                var window = manager.Create(new WindowCreateOptions(new AppId("test." + name), name, view, new CoreRect(10, 10, 960, 640)));
                attach(view, window); samples.Add((window, view, name));
            }
            static Task Done() => Task.CompletedTask;
            foreach (var style in new[] { SystemStyleIds.WindowsLike, SystemStyleIds.MacOsLike, SystemStyleIds.UbuntuLike })
            foreach (var dark in new[] { false, true })
            foreach (var language in new[] { "zh-CN", "en-US", "ja-JP" })
            foreach (var width in new[] { 960, 640, 320 })
            foreach (var (window, view, name) in samples)
            {
                settings.SystemStyleId = style; settings.Appearance = settings.Appearance with { Mode = dark ? ThemeKind.Dark : ThemeKind.Light }; settings.Language = language;
                foreach (var sample in samples) sample.window.View.IsVisible = ReferenceEquals(sample.window, window);
                window.View.Width = width; window.Info.Bounds = new CoreRect(10, 10, width, 640); Pump(); host.UpdateLayout(); Pump();
                using var frame = host.CaptureRenderedFrame();
                Check(window.View.HasTitleBarContent, $"{name}: no fused header.");
                if (name is not ("browser" or "notepad" or "taskmanager"))
                {
                    Check(window.View.HeaderLeading is Image { Source: not null }, $"{name}: workspace icon binding was lost after reparenting.");
                    Check(window.View.HeaderCenter is TextBlock { Text.Length: > 0 }, $"{name}: workspace title binding was lost after reparenting.");
                }
                if (view.FindControl<Grid>("HeaderHost") is { } oldHeader && name != "application-deployments")
                    Check(oldHeader.Bounds.Height < 1, $"{name}: duplicate header row.");
                Check(!view.GetVisualDescendants().Any(child => child is Control control && control.Classes.Contains("app-header")),
                    $"{name}: duplicate identity bar remains.");
                if (view.FindControl<Grid>("WorkspaceBody") is { } body)
                    Check(body.ColumnDefinitions.Count == (view.Bounds.Width < 620 ? 1 : 2), $"{name}: sidebar still consumes the narrow content viewport.");
                var controls = window.View.GetVisualDescendants().OfType<Grid>().Single(control => control.Name == "PART_WindowControls");
                var title = window.View.HeaderCenter ?? window.View.HeaderTabs!;
                var rect = new Rect(title.TranslatePoint(default, window.View)!.Value, title.Bounds.Size);
                Check(rect.Intersect(new Rect(controls.TranslatePoint(default, window.View)!.Value, controls.Bounds.Size)).Width <= 1,
                    $"{name}/{style}/{language}/{width}: title overlaps caption controls.");
                foreach (var slot in new[] { window.View.HeaderLeading, window.View.HeaderCenter, window.View.HeaderTrailing, window.View.HeaderTabs, window.View.HeaderToolbar }.OfType<Control>())
                foreach (var action in slot.GetVisualDescendants().OfType<Button>().Concat(slot is Button self ? [self] : Array.Empty<Button>())
                    .Where(button => button.IsEffectivelyVisible && button.IsEffectivelyEnabled && button.Bounds.Width > 0))
                {
                    if (action.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault() is { IsSelected: false }) continue;
                    var point = action.TranslatePoint(new Point(action.Bounds.Width / 2, action.Bounds.Height / 2), window.View)!.Value;
                    var hit = window.View.GetVisualAt(point);
                    Check(ReferenceEquals(hit, action) || hit?.GetVisualAncestors().Contains(action) == true,
                        $"{name}/{style}/{width}: header button {action.Name} is covered by {hit?.GetType().Name}.");
                }
                if (language == "en-US" && !dark && width is 960 or 320)
                {
                    var directory = Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "fused-workspaces"); Directory.CreateDirectory(directory);
                    using var image = new RenderTargetBitmap(new PixelSize(width + 20, 660)); image.Render(host);
                    image.Save(Path.Combine(directory, $"{name}-{style}-{(dark ? "dark" : "light")}-{width}.png"), new PngBitmapEncoderOptions());
                }
            }
            git.IsPickerMode = true; Pump(); Check(!gitWindow.View.HasTitleBarContent, "Git picker retained application chrome.");
            git.IsPickerMode = false; Pump(); Check(gitWindow.View.HasTitleBarContent, "Git repository did not restore fused chrome.");
            var identity = gitView.FindControl<WorkspaceWindowHeader>("Header")!;
            identity.Title = "Updated identity"; Pump();
            Check(((TextBlock)gitWindow.View.HeaderCenter!).Text == "Updated identity", "Detached title no longer follows identity changes.");
            gitView.AttachWindowHeader(gitWindow); Pump();
            Check(((TextBlock)gitWindow.View.HeaderCenter!).Text == "Updated identity", "Repeated header attachment changed bindings.");
            serverWindow.View.IsVisible = true; gitWindow.View.IsVisible = false;
            var compactNavigation = serverView.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "CompactWorkspaceMenu");
            compactNavigation.Flyout!.ShowAt(compactNavigation); Pump();
            var menu = (MenuFlyout)compactNavigation.Flyout;
            Check(menu.Items.Count == 3 && menu.Items.OfType<MenuItem>().All(item => item.Header is string { Length: > 0 }), "Compact workspace navigation lost localized page entries.");
            menu.Items.OfType<MenuItem>().Last().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent)); Pump();
            Check(serverView.FindControl<ContentControl>("ContentHost")!.Content is ServerCenterHistoryPage, "Compact navigation did not invoke the existing page action.");
            Console.WriteLine("PASS: 14 fused application views in 3 recipes, themes, languages and widths; Git picker transitions and detached bindings.");
        }
        finally { browserView.ClosePlatformBrowser(); host.Close(); tasksModel.DisposeAsync().AsTask().GetAwaiter().GetResult(); settings.Apply(original); Pump(); }
    }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

public class HeaderPreviewPermissions : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var type = method!.ReturnType;
        if (type == typeof(bool)) return false;
        if (type == typeof(Task)) return Task.FromException(new HttpRequestException("Offline preview fixture."));
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            if (method.Name == "GetTrafficAsync") return Task.FromResult(new RelaxKonOS.Protocol.Proxy.ProxyTrafficDto(0, 0, 0, 0, 0));
            return typeof(Task).GetMethods().Single(candidate => candidate.Name == nameof(Task.FromException) && candidate.IsGenericMethod)
                .MakeGenericMethod(type.GetGenericArguments()[0]).Invoke(null, [new HttpRequestException("Offline preview fixture.")]);
        }
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
