using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Settings.ViewModels;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.Services.SystemUi;
using RelaxKonOS.Client.Views;
using RelaxKonOS.Client.ViewModels.Shell;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Runtime;
using RelaxKonOS.WindowManager;
using WindowManagerService = RelaxKonOS.WindowManager.WindowManager;

internal static class MemoryLifecycleChecks
{
    private const string Icon = "avares://RelaxKonOS.Client/Assets/AppIcons/terminal.png";

    public static void Run(ShellSettings settings, LocalizationService localization, IServiceProvider services)
    {
        var changes = 0;
        EventHandler changed = (_, _) => changes++;
        settings.DesktopDisplayChanged += changed;
        var baseline = settings.ToPreferences();
        for (var i = 0; i < 200; i++) settings.Apply(baseline);
        Require(changes == 0, "Unchanged preference polls do not rebuild the desktop.");
        settings.Apply(baseline with { DesktopDisplay = baseline.DesktopDisplay! with { VisibleAppIds = ["relaxkonos.terminal"] } });
        Require(changes == 1, "A changed desktop filter still refreshes the desktop.");
        settings.Apply(baseline);
        settings.DesktopDisplayChanged -= changed;

        var alive = new LanguageProbe(localization);
        var languageBefore = localization.CurrentLanguage;
        var nextLanguage = languageBefore == "en-US" ? "ja-JP" : "en-US";
        settings.Language = nextLanguage;
        Require(alive.Changes > 0, "Weak localization listeners still update live view models.");
        var released = CreateLanguageProbe(localization);
        Collect();
        Require(!released.IsAlive, "Localization does not retain an abandoned view model.");
        GC.KeepAlive(alive);

        var page = new PageProbe(settings);
        settings.TimeFormat = settings.TimeFormat == "12h" ? "24h" : "12h";
        Require(page.Changes > 0, "Settings pages receive changes while open.");
        page.Dispose();
        page.Dispose();
        var disposedChanges = page.Changes;
        settings.TimeFormat = baseline.TimeFormat;
        settings.Language = languageBefore;
        Require(page.Changes == disposedChanges, "Disposed settings pages stop receiving global changes.");
        var releasedPage = CreateDisposedPage(settings);
        Collect();
        Require(!releasedPage.IsAlive, "Disposed settings pages can be collected.");
        CheckClosedHost(localization, services);

        var windows = new WindowManagerService();
        windows.Attach(new Canvas());
        var window = windows.Create(new WindowCreateOptions(new AppId("memory.window"), "Memory test",
            new Border(), new RelaxKonOS.Core.Primitives.Rect(0, 0, 400, 300), IconPath: Icon));
        var image = window.IconImage;
        Require(image is Bitmap, "The window loaded a real icon.");
        for (var i = 0; i < 100; i++) { windows.Minimize(window); windows.Restore(window); }
        Require(ReferenceEquals(image, window.IconImage), "Window state changes reuse the decoded icon.");
        windows.Close(window);
        Require(window.IconImage is null && !window.Thumbnail.HasImage, "Closing a window releases its images.");

        CheckDesktopEntries(settings, services);
        CheckIconDisposal(services);
        settings.Apply(baseline);
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine("PASS: desktop polling, image ownership, weak localization and settings-page disposal.");
    }

    private static void CheckDesktopEntries(ShellSettings settings, IServiceProvider services)
    {
        var windows = new WindowManagerService();
        var apps = new ApplicationManager(windows, services);
        apps.Register(new IconApplication("First"));
        var ssh = new SshDesktopSession(null!);
        // Avoid network or user-system-drive access: exercise the actual SSH desktop's app list.
        typeof(SshDesktopSession).GetProperty(nameof(SshDesktopSession.Endpoint))!.SetValue(ssh,
            ServerCenterSshEndpoint.Create("127.0.0.1", 22, "memory-test"));
        var session = System.Reflection.DispatchProxy.Create<IAuthSession, UnusedPreviewServices>();
        var desktop = new DesktopShellViewModel(windows, apps, settings,
            services.GetRequiredService<LocalizationService>(), session, ssh, () => { },
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
        desktop.PopulateDesktop();
        var entry = desktop.StartApps.Single();
        entry.IsDesktopSelected = true;
        for (var i = 0; i < 200; i++) desktop.PopulateDesktop();
        Require(ReferenceEquals(entry, desktop.StartApps.Single()) && entry.IsDesktopSelected,
            "Repeated desktop refreshes reuse the icon entry and preserve selection.");
        apps.Register(new IconApplication("Renamed"));
        desktop.PopulateDesktop();
        Require(!ReferenceEquals(entry, desktop.StartApps.Single()) && desktop.StartApps.Single().IconGlyph == "Renamed",
            "Application metadata changes replace the displayed entry.");
        apps.Unregister(new AppId("relaxkonos.terminal"));
        desktop.PopulateDesktop();
        Require(desktop.StartApps.Count == 0, "Removed applications leave the desktop.");
        Dispatcher.UIThread.RunJobs();
    }

    private static void CheckIconDisposal(IServiceProvider services)
    {
        var apps = new ApplicationManager(new WindowManagerService(), services);
        var icons = new[] { "terminal", "explorer", "settings", "welcome", "browser", "docker" };
        using (var warmup = new AppEntryViewModel(new ApplicationInfo(new AppId("memory.icon"), "Icon", IconPath: Icon), apps)) { }
        Collect();
        var before = PrivateBytes();
        for (var round = 0; round < 200; round++)
            foreach (var icon in icons)
            {
                using var entry = new AppEntryViewModel(new ApplicationInfo(new AppId("memory.icon"), "Icon",
                    IconPath: $"avares://RelaxKonOS.Client/Assets/AppIcons/{icon}.png"), apps);
                Require(entry.IconImage is Bitmap, "The stress-test icon decoded successfully.");
            }
        var growth = PrivateBytes() - before;
        // Do not force GC here: native bitmap ownership must work without finalizer help.
        Require(growth < 96 * 1024 * 1024, $"Repeated icon entry disposal stays bounded (growth {growth / 1048576.0:F1} MB).");
        Console.WriteLine($"Icon lifetime stress: 1,200 loads/disposals, private-memory growth {growth / 1048576.0:F1} MB.");
    }

    private static void CheckClosedHost(LocalizationService localization, IServiceProvider originalServices)
    {
        var windows = new WindowManagerService();
        using var overview = new WindowOverviewController(windows);
        using var coordinator = new SystemUiCoordinator(windows, overview);
        using var services = new ServiceCollection().AddSingleton(localization).AddSingleton(coordinator).BuildServiceProvider();
        var servicesProperty = typeof(RelaxKonOS.Client.App).GetProperty(nameof(RelaxKonOS.Client.App.Services))!;
        servicesProperty.SetValue(null, services);
        try
        {
            var released = CreateClosedHost();
            Dispatcher.UIThread.RunJobs();
            Collect();
            Require(!released.IsAlive, "Global language and system-UI services do not retain a closed host window.");
            GC.KeepAlive(services);
        }
        finally { servicesProperty.SetValue(null, originalServices); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateClosedHost()
    {
        var window = new MainWindow();
        window.Show();
        window.Close();
        return new WeakReference(window);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateLanguageProbe(LocalizationService localization) => new(new LanguageProbe(localization));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateDisposedPage(ShellSettings settings)
    {
        var page = new PageProbe(settings);
        page.Dispose();
        return new WeakReference(page);
    }

    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    private static long PrivateBytes() { using var process = Process.GetCurrentProcess(); return process.PrivateMemorySize64; }
    private static void Require(bool valid, string message) { if (!valid) throw new Exception(message); }

    private sealed class LanguageProbe : LocalizedObservableObject
    {
        public int Changes { get; private set; }
        public LanguageProbe(LocalizationService localization) : base(localization) => PropertyChanged += (_, _) => Changes++;
    }

    private sealed class PageProbe : SettingsPageViewModel
    {
        public PageProbe(ShellSettings settings) : base(settings, null) => PropertyChanged += (_, _) => Changes++;
        public int Changes { get; private set; }
        public override string Route => "memory";
        public override string DisplayNameKey => "memory";
        public override string DisplayName => "Memory";
    }

    private sealed class IconApplication(string title) : RemoteApplicationBase
    {
        public override ApplicationManifest Manifest => new(new AppId("relaxkonos.terminal"), title, IconGlyph: title, IconPath: Icon);
    }
}
