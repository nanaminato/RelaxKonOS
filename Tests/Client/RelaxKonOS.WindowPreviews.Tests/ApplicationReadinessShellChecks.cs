using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.ViewModels.Shell;
using RelaxKonOS.Client.Views.Shell;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Runtime;
using RelaxKonOS.Shell;

internal static class ApplicationReadinessShellChecks
{
    public static void Run(ShellSettings settings, LocalizationService localization, IServiceProvider services)
    {
        var previous = settings.ToPreferences([]);
        var windows = new RelaxKonOS.WindowManager.WindowManager();
        windows.Attach(new Canvas { Width = 1000, Height = 720 });
        var applications = new ApplicationManager(windows, services);
        foreach (var id in new[] { "relaxkonos.proxy-server", "relaxkonos.component-center", "relaxkonos.settings", "test.no-component" })
            applications.Register(new ReadinessApplication(id));
        var availability = new ReadinessProvider();
        var session = DispatchProxy.Create<IAuthSession, UnusedPreviewServices>();
        var vm = new DesktopShellViewModel(windows, applications, settings, localization, session,
            new SshDesktopSession(null!, new ShellSettings(null!)), () => { },
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, availability);
        var state = new ShellStateStore(); state.Publish(vm);
        using var adapter = new DesktopShellStateAdapter(vm, state, new NoShellCatalog());
        var appId = new AppId("relaxkonos.proxy-server");
        try
        {
            settings.ShowBuiltInApps = true; settings.VisibleAppIds = []; settings.OnlyReadyApplications = false;
            vm.PopulateDesktop(); Dispatcher.UIThread.RunJobs();
            Check(vm.StartApps.Any(app => app.Id == appId) && vm.StartApps.Single(app => app.Id == appId).HasAvailabilityBadge,
                "Unknown dependencies stay discoverable with a separate state badge by default");
            availability.Set(ComponentAvailabilityKind.Missing); Dispatcher.UIThread.RunJobs();
            Check(adapter.Current.Applications.Single(app => app.Id == appId).Availability is { State: "Missing", IsReady: false },
                "External shells receive the same host-resolved component badge");
            settings.PinnedAppIds = [appId.Value];
            settings.OnlyReadyApplications = true; Dispatcher.UIThread.RunJobs();
            Check(!vm.StartApps.Any(app => app.Id == appId) && !vm.DesktopIcons.Any(app => app.Id == appId),
                "Ready-only mode filters both launch lists and desktop icons");
            Check(vm.StartApps.Any(app => app.Id.Value == "relaxkonos.component-center")
                && vm.StartApps.Any(app => app.Id.Value == "relaxkonos.settings")
                && vm.StartApps.Any(app => app.Id.Value == "test.no-component"),
                "Component Center, Settings and apps without independent components remain visible");
            Check(applications.IsRegistered(appId) && vm.TaskbarGroups.Any(group => group.AppId == appId),
                "Readiness filtering neither unregisters the application nor removes pinned launch paths");
            vm.StartSearchQuery = "proxy";
            Check(!vm.StartSearchResults.Any(app => app.Id == appId), "Application search follows readiness filtering");
            availability.Set(ComponentAvailabilityKind.Stopped); Dispatcher.UIThread.RunJobs();
            Check(vm.StartApps.Any(app => app.Id == appId), "A stopped installed service returns to the ready list");
            availability.Set(null); Dispatcher.UIThread.RunJobs();
            Check(!vm.StartApps.Any(app => app.Id == appId), "Invalidating host observations removes stale readiness");
            settings.OnlyReadyApplications = false; Dispatcher.UIThread.RunJobs();
            Check(vm.StartApps.Any(app => app.Id == appId), "Disabling the filter restores discoverability without installing anything");
            Check(settings.ToPreferences([]).DesktopDisplay!.OnlyReadyApplications == false,
                "Ready-only state is projected to the workspace preference contract");
            availability.Set(ComponentAvailabilityKind.Missing); vm.StartSearchQuery = ""; Dispatcher.UIThread.RunJobs();
            Render(vm, settings, state, windows);
        }
        finally { settings.Apply(previous); Dispatcher.UIThread.RunJobs(); }
    }
    private static void Render(DesktopShellViewModel vm, ShellSettings settings, ShellStateStore state,
        RelaxKonOS.WindowManager.WindowManager windows)
    {
        var directory = Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "readiness"); Directory.CreateDirectory(directory);
        foreach (var language in new[] { "zh-CN", "en-US", "ja-JP" })
        foreach (var dark in new[] { false, true })
        foreach (var kind in new[] { "windows", "macos", "ubuntu" })
        {
            settings.Language = language; settings.Appearance = settings.Appearance with { Mode = dark ? ThemeKind.Dark : ThemeKind.Light };
            Dispatcher.UIThread.RunJobs(); vm.IsStartOpen = true;
            LauncherDesktopShellBase shell = kind switch { "windows" => new WindowsLikeDesktopShell(), "macos" => new MacosLikeDesktopShell(), _ => new UbuntuLikeDesktopShell() };
            var surfaces = new PreviewSurfaceRegistry(windows);
            shell.InitializeAsync(new ShellPresentationContext(state, null!, null!, surfaces, null!), default).GetAwaiter().GetResult();
            var host = new Window { Width = 1000, Height = 720, Content = shell.View };
            host.Show(); Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
            var expected = vm.AllApplications.Single(app => app.Id.Value == "relaxkonos.proxy-server").AvailabilityText.Resolve();
            Check(shell.View.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == expected),
                kind + " displays the separate localized dependency label in " + language);
            using var bitmap = new RenderTargetBitmap(new PixelSize(1000, 720)); bitmap.Render(host);
            bitmap.Save(Path.Combine(directory, kind + "-" + language + (dark ? "-dark.png" : "-light.png")), new PngBitmapEncoderOptions());
            host.Close(); shell.DisposeAsync().GetAwaiter().GetResult(); Dispatcher.UIThread.RunJobs();
        }
        Console.WriteLine("Readiness shell previews: " + directory);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: Readiness shell: " + message);
    }
    private sealed class ReadinessProvider : IApplicationAvailabilityProvider
    {
        private ApplicationAvailability? _value;
        public event EventHandler? Changed;
        public bool RequiresCheck(ApplicationManifest manifest) => manifest.Id.Value == "relaxkonos.proxy-server";
        public ApplicationAvailability? GetCached(AppId app) => app.Value == "relaxkonos.proxy-server" ? _value : null;
        public Task<ApplicationAvailability?> RefreshAsync(ApplicationManifest manifest) => Task.FromResult(_value);
        public void Set(ComponentAvailabilityKind? state)
        {
            _value = state is { } value ? new(new("relaxkonos.proxy-server"), "server-a", "sing-box", value, "host-a", Guid.NewGuid()) : null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
    private sealed class ReadinessApplication(string id) : IRemoteApplication
    {
        public ApplicationManifest Manifest { get; } = new(new(id), id, IconGlyph: "A");
        public void Activate(RelaxKonOS.AppSDK.AppContext context) => context.ShowWindow(id, new Border());
    }
    private sealed class NoShellCatalog : IShellCatalog
    {
        public IReadOnlyList<ShellDescriptor> Available => [];
        public event EventHandler? Changed { add { } remove { } }
        public bool TryGet(string id, out ShellDescriptor descriptor) { descriptor = null!; return false; }
    }
}
