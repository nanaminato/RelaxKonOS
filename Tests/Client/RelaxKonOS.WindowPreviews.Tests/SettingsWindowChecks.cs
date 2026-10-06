using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Apps.Browser;
using RelaxKonOS.Client.Apps.Docker;
using RelaxKonOS.Client.Apps.Settings.ViewModels;
using RelaxKonOS.Client.Apps.Settings.Views;
using RelaxKonOS.Client.Apps.TaskManager;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.Diagnostics;
using RelaxKonOS.Client.Services.HostSettings;
using RelaxKonOS.Client.Services.Theming;
using RelaxKonOS.Client.Services.WorkspaceSettings;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.Settings;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Runtime;
using RelaxKonOS.Client.Views.Shell;
using RelaxKonOS.Shell;

internal static class SettingsWindowChecks
{
    public static void Run(ShellSettings settings, LocalizationService localization)
    {
        var previousServices = RelaxKonOS.Client.App.Services;
        var original = settings.ToPreferences();
        var session = DispatchProxy.Create<IAuthSession, SettingsWindowSession>();
        var auth = (SettingsWindowSession)session;
        using var http = new HttpClient(new NoSettingsNetwork());
        using var services = new ServiceCollection().AddSingleton(new DesktopDevicePreferences(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "desktop-device.json")))
            .AddSingleton(localization)
            .AddSingleton<IHostTimeService>(DispatchProxy.Create<IHostTimeService, SettingsWindowHostService>())
            .AddSingleton<IHostIdentityService>(DispatchProxy.Create<IHostIdentityService, UnusedPreviewServices>())
            .AddSingleton<IHostNetworkService>(DispatchProxy.Create<IHostNetworkService, UnusedPreviewServices>())
            .AddSingleton(new AccountSecurityClient(http, session))
            .AddSingleton<IRememberedSessionStore>(DispatchProxy.Create<IRememberedSessionStore, UnusedPreviewServices>())
            .AddSingleton<IOwnerDevicePairingEndpointStore>(DispatchProxy.Create<IOwnerDevicePairingEndpointStore, UnusedPreviewServices>())
            .AddSingleton<IShellCatalog>(new SettingsWindowShellCatalog())
            .AddSingleton<ISystemStyleRegistry>(new SystemStyleRegistry())
            .AddSingleton<IRemoteDockerClient>(DispatchProxy.Create<IRemoteDockerClient, UnusedPreviewServices>()).BuildServiceProvider();
        typeof(RelaxKonOS.Client.App).GetProperty(nameof(RelaxKonOS.Client.App.Services))!.SetValue(null, services);
        var windows = new RelaxKonOS.WindowManager.WindowManager();
        var apps = new ApplicationManager(windows, services);
        var developer = new DeveloperModeService();
        using var diagnostics = new NetworkDiagnosticsService(developer, () => session);
        using var inspector = new NetworkInspectorWindowService(windows, diagnostics, localization);
        var registry = new DefaultAppRegistry();
        var preferenceService = new SettingsWindowPreferences();
        using var editor = new WorkspacePreferencesEditor(preferenceService, session, registry);
        using var model = new SettingsViewModel(settings, preferenceService, session, editor, apps,
            DispatchProxy.Create<IRelaxKonOSClient, UnusedPreviewServices>(),
            DispatchProxy.Create<ITaskManagerClient, UnusedPreviewServices>(), registry, developer, null,
            DispatchProxy.Create<IBrowserClient, UnusedPreviewServices>(), inspector, localization);
        var view = new SettingsView { DataContext = model };
        var window = new Window { Width = 640, Height = 480, Content = view };
        try
        {
            settings.Language = "zh-CN";
            model.InitializeAsync().GetAwaiter().GetResult(); // Offline: no real host calls.
            window.Show(); Pump();
            var menu = view.FindControl<Button>("NavigationMenuButton")!;
            var drawer = view.FindControl<Grid>("NavigationDrawer")!;
            var scroll = view.FindControl<ScrollViewer>("SettingsPageScroll")!;
            Click(window, menu); Check(drawer.IsVisible, "Compact menu did not open.");
            Check(ReferenceEquals(window.FocusManager?.GetFocusedElement(), view.FindControl<Button>("CloseNavigationButton")), "Drawer did not take focus.");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump();
            Check(!drawer.IsVisible && ReferenceEquals(window.FocusManager?.GetFocusedElement(), menu), "Escape did not close drawer and restore focus.");
            Click(window, menu);
            view.FindControl<ListBox>("DrawerNavigationList")!.ScrollIntoView(model.NavigationPages.Single(page => page.Route == "time-language")); Pump();
            var timeButton = drawer.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Tag, "time-language"));
            Click(window, timeButton);
            Check(!drawer.IsVisible && model.SelectedPage?.Route == "time-language", "Drawer selection did not navigate and close.");
            scroll.Offset = new Vector(0, 120); Pump(); var rememberedOffset = scroll.Offset.Y;
            model.OpenPageCommand.Execute("about"); Pump();
            model.BackCommand.Execute(null); Pump();
            Check(Math.Abs(scroll.Offset.Y - rememberedOffset) < 1, "Returning to a page lost its scroll offset.");
            model.SearchQuery = "region"; Pump();
            var region = model.SearchResults.Single(entry => entry.SettingId == "workspace.region");
            model.OpenSearchResultCommand.Execute(region); Pump();
            Check(model.SearchLocationStatus.Length > 0 && scroll.Offset.Y > rememberedOffset,
                "Root settings search did not reveal the lower setting.");
            model.BackCommand.Execute(null); Pump();
            Check(model.SearchQuery == "region", "Same-page search Back lost the query.");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump();
            Check(model.SearchQuery.Length == 0 && Math.Abs(scroll.Offset.Y - rememberedOffset) < 1,
                $"Escape did not restore pre-search page position: query={model.SearchQuery}, offset={scroll.Offset.Y}, expected={rememberedOffset}.");
            window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, null); Pump();
            Check(ReferenceEquals(window.FocusManager?.GetFocusedElement(), view.FindControl<TextBox>("CompactSearchBox")), "Ctrl+F focused hidden wide search.");

            auth.State = AuthSessionState.Authenticated;
            model.OpenPageCommand.Execute("personalization/colors"); Pump();
            var colors = model.Pages.OfType<PersonalizationPageViewModel>().Single();
            var source = new PreferencesEditSource("relaxkonos.settings", "personalization/colors");
            colors.Theme = colors.Theme == RelaxKonOS.Protocol.Desktop.ThemeKind.Dark
                ? RelaxKonOS.Protocol.Desktop.ThemeKind.Light : RelaxKonOS.Protocol.Desktop.ThemeKind.Dark;
            Check(editor.Source == source && !model.HasLocalSaveFeedback && model.SaveStatus.Length == 0, "Saving should be silent.");
            preferenceService.Pending.TrySetException(new IOException("Controlled test failure"));
            PumpUntil(() => editor.State == PreferencesSaveState.Failed);
            Check(model.HasFailureToast && model.FailureToast.Length > 0, "Failed saves should show a transient toast.");
            model.OpenPageCommand.Execute("about"); Pump();
            Check(model.HasOtherSaveFeedback && model.CanOpenSaveSource && model.CanRetry && model.SaveSourceRoute == source.PageRoute,
                "Navigating away concealed a failed save or its source.");
            model.OpenPageCommand.Execute(model.SaveSourceRoute); Pump();
            Check(model.HasLocalSaveFeedback && !model.HasOtherSaveFeedback, "Returning to the source did not restore local failure feedback.");
            preferenceService.Pending = new();
            model.RetrySaveCommand.Execute(null);
            preferenceService.Pending.TrySetResult(settings.ToPreferences() with { Revision = 25, PersistedRevision = null });
            PumpUntil(() => editor.State == PreferencesSaveState.Accepted);
            Check(!model.HasFailureToast, "Retry should clear the previous failure toast.");
            Check(!model.HasLocalSaveFeedback && editor.Source == source, "Accepted writes should remain silent and retain their source.");
            preferenceService.Value = preferenceService.Value with { PersistedRevision = 25 };
            PumpUntil(() => editor.State == PreferencesSaveState.Saved);
            Check(!model.HasLocalSaveFeedback, "Successful saves should remain silent.");
            model.OpenPageCommand.Execute("home"); Pump();
            Check(!model.HasOtherSaveFeedback, "A completed unrelated save clutters the home page.");
            auth.SwitchWorkspace(); Pump();
            Check(editor.Source is null && editor.State == PreferencesSaveState.Idle,
                "Switching authenticated workspaces retained completed save feedback.");
            preferenceService.Pending = new();
            editor.Schedule(settings.ToPreferences(), null);
            Check(editor.Source is null && !model.HasOtherSaveFeedback && !model.CanOpenSaveSource,
                "An unlabelled edit inherited a stale Settings page source.");
            auth.Logout(); Pump();
            Check(editor.Source is null && editor.State == PreferencesSaveState.Idle && !model.BackCommand.CanExecute(null),
                "Logout retained source feedback or old navigation history.");

            var output = Path.Combine(AppContext.BaseDirectory, "preview-qa", "settings");
            Directory.CreateDirectory(output);
            foreach (var language in new[] { "zh-CN", "en-US", "ja-JP" })
            {
                settings.Language = language; Pump();
                foreach (var size in new[] { new PixelSize(640, 480), new PixelSize(1024, 768), new PixelSize(1440, 900) })
                {
                    window.Width = size.Width; window.Height = size.Height; model.OpenPageCommand.Execute("home"); Pump();
                    Check(view.FindControl<Grid>("CompactNavigation")!.IsVisible == (size.Width < 760), "Responsive navigation chose the wrong mode.");
                    Capture(window, size, Path.Combine(output, $"window-home-{language}-{size.Width}x{size.Height}.png"));
                }
            }
            settings.Language = "zh-CN"; window.Width = 1024; window.Height = 768;
            foreach (var mode in new[] { RelaxKonOS.Protocol.Desktop.ThemeKind.Light, RelaxKonOS.Protocol.Desktop.ThemeKind.Dark })
            {
                settings.Appearance = settings.Appearance with { Mode = mode };
                model.OpenPageCommand.Execute("personalization/colors"); Pump();
                Capture(window, new PixelSize(1024, 768), Path.Combine(output, $"window-colors-{mode}.png"));
                model.OpenPageCommand.Execute("personalization"); Pump();
                Capture(window, new PixelSize(1024, 768), Path.Combine(output, $"window-personalization-{mode}.png"));
            }
            foreach (var language in new[] { "zh-CN", "en-US", "ja-JP" })
            {
                settings.Language = language; window.Width = 640; window.Height = 480;
                model.OpenPageCommand.Execute("personalization"); Pump();
                Check(scroll.Extent.Width <= scroll.Viewport.Width + 1, "Personalization requires horizontal scrolling.");
                Check(!localization.Get("settings.network.remote_title", "missing").Equals("missing"), "Missing remote network translation.");
                Capture(window, new PixelSize(640, 480), Path.Combine(output, $"window-personalization-{language}-640x480.png"));
            }
            settings.Language = "zh-CN";
            window.Width = 320; window.Height = 240;
            model.OpenPageCommand.Execute("time-language"); Pump();
            var timeTarget = SettingsSearchTarget.Find(view, "workspace.timeFormat")!;
            using (SettingsSearchTarget.HighlightAndFocus(timeTarget)) { Pump(); }
            var point = timeTarget.TranslatePoint(default, view)!.Value;
            Check(point.Y < view.Bounds.Height && point.Y + timeTarget.Bounds.Height > 0 && scroll.Viewport.Height > 0,
                "Magnified root frame prevents access to page controls.");
            Capture(window, new PixelSize(320, 240), Path.Combine(output, "window-time-language-small-viewport.png"));
            window.Width = 640; window.Height = 480; Pump();
            Click(window, menu); Check(drawer.IsVisible, "Drawer cannot reopen after repeated navigation.");
            window.Width = 1024; Pump(); Check(!drawer.IsVisible, "Wide layout retained the compact drawer.");
            Console.WriteLine("PASS: Full Settings window drawer, Escape/Ctrl+F, search/back/scroll, save failure/retry/durability/source reset, three languages and magnified access.");
        }
        finally
        {
            window.Close();
            typeof(RelaxKonOS.Client.App).GetProperty(nameof(RelaxKonOS.Client.App.Services))!.SetValue(null, previousServices);
            settings.Apply(original); Pump();
        }
    }
    private static void Click(Window window, Control target)
    {
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Pump();
    }
    private static void Pump() { Dispatcher.UIThread.RunJobs(); }
    private static void PumpUntil(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < timeout) { Thread.Sleep(10); Pump(); }
        Check(condition(), "Timed out waiting for controlled preference save."); Pump();
    }
    private static void Capture(Window window, PixelSize size, string path)
    {
        using var screenshot = window.CaptureRenderedFrame() ?? throw new Exception("Settings window did not render a frame.");
        screenshot.Save(path, PngBitmapEncoderOptions.Default);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}

public class SettingsWindowSession : DispatchProxy
{
    public AuthSessionState State = AuthSessionState.Unauthenticated;
    private event EventHandler<AuthSessionStateChangedEventArgs>? Changed;
    private readonly AuthTokens tokens = new("test", "test", DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddDays(1));
    private WorkspaceDto workspace = new(Guid.NewGuid(), Guid.NewGuid(), "Test workspace", WorkspaceState.Running, DateTimeOffset.UtcNow, null);
    private readonly SessionDto session = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, SessionStatus.Active, Guid.NewGuid(), "test", DateTimeOffset.UtcNow);
    public void SwitchWorkspace() { workspace = workspace with { Id = Guid.NewGuid() }; Changed?.Invoke(this, new(State)); }
    public void Logout() { State = AuthSessionState.Unauthenticated; Changed?.Invoke(this, new(State)); }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "add_StateChanged") { Changed += (EventHandler<AuthSessionStateChangedEventArgs>)args![0]!; return null; }
        if (method.Name == "remove_StateChanged") { Changed -= (EventHandler<AuthSessionStateChangedEventArgs>)args![0]!; return null; }
        return method.Name switch
        {
            "get_State" => State, "get_ServiceId" => "test-service", "get_EffectiveBaseUrl" => "http://test.invalid/",
            "get_Tokens" => tokens, "get_CurrentWorkspace" => workspace, "get_CurrentSession" => session,
            _ when method.Name.StartsWith("get_") => method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null,
            _ => throw new InvalidOperationException(method.Name)
        };
    }
}
public class SettingsWindowHostService : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "CaptureConnection" => new HostSettingsConnection("test-service", Guid.Empty, Guid.Empty),
        "CatalogAsync" => Task.FromResult(new SettingsCatalogSnapshot([], DateTimeOffset.UtcNow)),
        "IsCurrent" => true,
        _ => throw new InvalidOperationException("Unexpected host call: " + method.Name)
    };
}
internal sealed class SettingsWindowPreferences : IWorkspaceSettingsService
{
    public TaskCompletionSource<WorkspacePreferencesDto> Pending = new();
    public WorkspacePreferencesDto Value = WorkspacePreferencesDto.Default;
    public Task<WorkspacePreferencesDto> GetAsync(string url, string token, Guid workspace, CancellationToken ct = default) => Task.FromResult(Value);
    public async Task<WorkspacePreferencesDto> SaveAsync(string url, string token, Guid workspace, WorkspacePreferencesDto value, CancellationToken ct = default)
        => Value = await Pending.Task.WaitAsync(ct);
}
internal sealed class SettingsWindowShellCatalog : IShellCatalog
{
    public IReadOnlyList<ShellDescriptor> Available => BuiltInShells.All;
    public event EventHandler? Changed { add { } remove { } }
    public bool TryGet(string id, out ShellDescriptor descriptor)
    { descriptor = Available.FirstOrDefault(item => item.Id == id)!; return descriptor is not null; }
}
internal sealed class NoSettingsNetwork : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Full Settings UI checks must not make HTTP requests.");
}
