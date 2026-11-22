using RelaxKonOS.App.ComponentCenter;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.App.Settings.Views;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.LogicalTree;
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
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.Diagnostics;
using RelaxKonOS.Client.Services.HostSettings;
using RelaxKonOS.Client.Services.Theming;
using RelaxKonOS.Client.Services.WorkspaceSettings;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.Settings;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Protocol.Workspace.SystemStyles;
using RelaxKonOS.Runtime;
using RelaxKonOS.AppSDK;
using AppContext = System.AppContext;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Client.Views.Shell;
using RelaxKonOS.Shell;

internal static class SettingsWindowChecks
{
    public static void Run(ShellSettings settings, LocalizationService localization, string? packageSource = null, bool componentCenter = false)
    {
        const string pathValue = "/usr/bin::/bin";
        EnvironmentMutation? mutation = null;
        var pathVariable = new EnvironmentVariable("PATH", pathValue, null, EnvironmentValueKind.ExpandString,
            SettingsScope.HostMachine, false, false, []);
        var variableEditor = EnvironmentVariableEditor.Create(pathVariable, false, key => key,
            () => Task.FromResult<string?>(null), () => { }, value => mutation = value);
        var saveVariable = variableEditor.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "common.save"));
        saveVariable.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Check(mutation == new EnvironmentMutation("PATH", EnvironmentMutationKind.Set, pathValue, EnvironmentValueKind.ExpandString),
            "The PATH editor lost an empty segment or the existing value kind.");

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
            .AddSingleton<RelaxKonOS.Client.Services.Modules.IServerModuleCatalog>(new EmptyModuleCatalog())
            .AddSingleton<IRemoteDockerClient>(DispatchProxy.Create<IRemoteDockerClient, UnusedPreviewServices>()).BuildServiceProvider();
        typeof(RelaxKonOS.Client.App).GetProperty(nameof(RelaxKonOS.Client.App.Services))!.SetValue(null, services);
        var windows = new RelaxKonOS.WindowManager.WindowManager();
        var memory = new UsageMemoryStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "usage.json"));
        ExternalAppContextFactory CreateFactory(RelaxKonOS.App.Settings.ISettingsWindow workspace, IComponentCenterWorkspace? centerWorkspace) =>
            new(null!, null!, localization, settings, null!, session,
            null!, windows, null!, new RelaxKonOS.Client.Apps.Explorer.ExplorerClient(http, session, memory),
            new RelaxKonOS.Client.Services.ServerCenter.SshDesktopSession(null!, settings), null!, null!, null!,
            null!, null!, memory, null!, null!, null!, null!,
            settingsWindow: workspace,
            componentCenterWorkspace: centerWorkspace ?? new RelaxKonOS.Client.Apps.ComponentCenter.ComponentCenterHostWorkspace(services));
        var factory = CreateFactory(new RelaxKonOS.Client.Apps.Settings.SettingsWindow(services), null);
        var settingsManifest = new RelaxKonOS.App.Settings.SettingsPackageApp().Manifest;
        using var packageLifetime = new CancellationTokenSource();
        var official = RelaxKonOS.Client.Services.Developer.PackageOrigin.OfficialDistribution;
        var local = RelaxKonOS.Client.Services.Developer.PackageOrigin.LocalPackage;
        var settingsUri = new Uri("relaxkonos://settings/home");
        Check(OfficialDesktopPackages.OwnsShellRoute(settingsManifest.Id.Value, official, settingsUri)
            && !OfficialDesktopPackages.OwnsShellRoute(settingsManifest.Id.Value, local, settingsUri)
            && !OfficialDesktopPackages.OwnsShellRoute("relaxkonos.other", official, settingsUri)
            && !OfficialDesktopPackages.OwnsShellRoute(settingsManifest.Id.Value, official, new Uri("relaxkonos://applications/home")),
            "The official Settings package's Shell route ownership was not scoped to its ID, origin and host.");
        var capability = ((RelaxKonOS.App.Settings.ISettingsPackageContext)
            factory.CreateForInstalledPackage(settingsManifest, official, packageLifetime.Token, null, null)).SettingsWindow;
        Check(capability is not null, "Official Settings did not receive the workspace capability.");
        Check(((RelaxKonOS.App.Settings.ISettingsPackageContext)factory.Create(settingsManifest)).SettingsWindow is null
            && ((RelaxKonOS.App.Settings.ISettingsPackageContext)factory.CreateForInstalledPackage(settingsManifest, local, default, null, null)).SettingsWindow is null
            && ((RelaxKonOS.App.Settings.ISettingsPackageContext)factory.CreateForInstalledPackage(
                settingsManifest with { Id = new AppId("relaxkonos.other") }, official, default, null, null)).SettingsWindow is null,
            "A workspace capability was exposed outside official Settings package activation.");
        packageLifetime.Cancel();
        try
        {
            capability!.Navigate("home");
            throw new Exception("A retired Settings package retained workspace access.");
        }
        catch (OperationCanceledException) { }

        var centerManifest = new RelaxKonOS.App.ComponentCenter.ComponentCenterPackageApp().Manifest;
        var centerUri = new Uri("relaxkonos://components/server-modules?hostId=fixture");
        using var centerLifetime = new CancellationTokenSource();
        var center = ((IComponentCenterPackageContext)factory.CreateForInstalledPackage(
            centerManifest, official, centerLifetime.Token, null, null)).ComponentCenterWorkspace;
        Check(center?.CanNavigate(centerUri) == true
            && ((IComponentCenterPackageContext)factory.CreateForInstalledPackage(centerManifest, local, default, null, null)).ComponentCenterWorkspace is null
            && ((IComponentCenterPackageContext)factory.CreateForInstalledPackage(settingsManifest, official, default, null, null)).ComponentCenterWorkspace is null,
            "Only the official Component Center package receives its workspace capability.");
        Check(OfficialDesktopPackages.OwnsShellRoute(centerManifest.Id.Value, official, centerUri)
            && !OfficialDesktopPackages.OwnsShellRoute(centerManifest.Id.Value, local, centerUri)
            && !OfficialDesktopPackages.OwnsShellRoute(centerManifest.Id.Value, official, new Uri(centerUri + "&action=install")),
            "The Component Center package route must pass the existing host parser.");
        centerLifetime.Cancel();
        Check(center?.CanNavigate(centerUri) == false, "A retired Component Center package remained eligible for navigation.");

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
            DispatchProxy.Create<IBrowserData, UnusedPreviewServices>(), inspector, localization);
        var view = new SettingsWorkspaceView { DataContext = model };
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
            var aboutView = view.GetVisualDescendants().OfType<AboutPageView>().Single();
            Check(aboutView.GetVisualDescendants().OfType<TextBlock>().Any(label =>
                    label.Text == localization.Get("settings.about_page.description", "")),
                "The detached About page must render the current language.");
            settings.Language = "ja-JP"; Pump();
            Check(aboutView.GetVisualDescendants().OfType<TextBlock>().Any(label =>
                    label.Text == localization.Get("settings.about_page.description", "")),
                "An open About page must refresh its markup-based labels after a language change.");
            settings.Language = "zh-CN"; Pump();
            model.BackCommand.Execute(null); Pump();
            Check(Math.Abs(scroll.Offset.Y - rememberedOffset) < 1, "Returning to a page lost its scroll offset.");
            model.SearchQuery = "region"; Pump();
            var region = model.SearchResults.Single(entry => entry.SettingId == "workspace.region");
            var searchResults = view.GetVisualDescendants().OfType<SettingsSearchResultsView>().Single();
            var resultList = searchResults.GetVisualDescendants().OfType<ListBox>().Single();
            resultList.SelectedItem = region;
            resultList.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Pump();
            Check(model.SearchLocationStatus.Length > 0 && scroll.Offset.Y > rememberedOffset,
                "Root settings search did not reveal the lower setting.");
            model.BackCommand.Execute(null); Pump();
            Check(model.SearchQuery == "region", "Same-page search Back lost the query.");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump();
            Check(model.SearchQuery.Length == 0 && Math.Abs(scroll.Offset.Y - rememberedOffset) < 1,
                $"Escape did not restore pre-search page position: query={model.SearchQuery}, offset={scroll.Offset.Y}, expected={rememberedOffset}.");
            window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, null); Pump();
            Check(ReferenceEquals(window.FocusManager?.GetFocusedElement(), view.SearchBar.SearchBox), "Ctrl+F did not focus top search.");

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
            foreach (var width in new[] { 640, 1024, 1440, 1920 })
            {
                window.Width = width; window.Height = 900;
                foreach (var route in new[] { "system", "time-language", "personalization", "apps", "about" })
                {
                    model.OpenPageCommand.Execute(route); Pump();
                    var heading = view.FindControl<TextBlock>("PageHeading")!;
                    var content = view.FindControl<ContentControl>("PageContent")!;
                    var page = content.GetVisualDescendants().OfType<UserControl>().First();
                    var body = page.GetVisualDescendants().OfType<StackPanel>().First();
                    var headingLeft = heading.TranslatePoint(default, view)!.Value.X;
                    var bodyLeft = body.TranslatePoint(default, view)!.Value.X;
                    Check(Math.Abs(headingLeft - bodyLeft) < 1, $"{route} heading and content diverge at width {width}.");
                    Check(scroll.Extent.Width <= scroll.Viewport.Width + 1, $"{route} requires horizontal scrolling at width {width}.");
                    var pin = view.FindControl<Avalonia.Controls.Primitives.ToggleButton>("PinCurrentPageButton")!;
                    if (pin.IsVisible)
                    {
                        var pinRight = pin.TranslatePoint(new Point(pin.Bounds.Width, 0), scroll)!.Value.X;
                        Check(scroll.Bounds.Width - pinRight >= 24, "Pin action crowds the scrollbar.");
                    }
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
            model.OpenPageCommand.Execute("personalization/colors"); Pump();
            var parentBreadcrumb = view.FindControl<Button>("ParentBreadcrumb")!;
            Check(parentBreadcrumb.IsVisible && Equals(parentBreadcrumb.Content, model.ParentTitle), "Large breadcrumb does not identify parent.");
            Click(window, parentBreadcrumb);
            Check(model.SelectedPage?.Route == "personalization", "Parent breadcrumb did not navigate.");
            model.OpenPageCommand.Execute("personalization/colors"); Pump();
            Click(window, view.Header.BackButton!);
            Check(model.SelectedPage?.Route == "personalization", "Top title-bar back arrow did not navigate.");
            window.Content = null;
            var canvas = new Canvas();
            window.Content = canvas; window.Width = 1100; window.Height = 800;
            windows.Attach(canvas);
            windows.SetHostBounds(new RelaxKonOS.Core.Primitives.Rect(0, 0, 1100, 800));
            var managed = windows.Create(new RelaxKonOS.WindowManager.WindowCreateOptions(
                new RelaxKonOS.Core.Applications.AppId("relaxkonos.settings"), "Settings", view,
                new RelaxKonOS.Core.Primitives.Rect(0, 0, 1080, 780)));
            view.AttachWindowHeader(managed);
            model.OpenPageCommand.Execute("personalization/colors"); Pump();
            Check(ReferenceEquals(managed.View.HeaderLeading, view.Header)
                  && ReferenceEquals(managed.View.HeaderCenter, view.SearchBar) && managed.View.HeaderTrailing is null,
                "Settings blocks were not handed to the host's title-bar role slots.");
            Check(managed.View.HasTitleBarContent, "Role slots left the host title bar unfused.");
            var titleBar = managed.View.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_TitleBar");
            // The fused bar takes its height from the style rather than from a literal in the window
            // manager, so the assertion reads the same resource the host wrote.
            var fusedHeight = (double)managed.View.FindResource("WindowFusedTitleBarHeight")!;
            Check(Math.Abs(titleBar.Bounds.Height - fusedHeight) < 1 && view.Header.IsEffectivelyVisible && view.SearchBar.IsEffectivelyVisible,
                $"Embedded title bar has no usable single-layer layout ({titleBar.Bounds.Height}, expected {fusedHeight}).");
            Click(window, view.SearchBar.SearchBox);
            var oldBounds = managed.Info.Bounds;
            view.SearchBar.SearchBox.Text = "wallpaper"; Pump();
            Check(model.HasSearch && managed.Info.Bounds == oldBounds, "Title-bar search input moved the window or lost binding.");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump();
            Check(!model.HasSearch, "Escape in embedded search did not restore content.");
            Capture(window, new PixelSize(1100, 800), Path.Combine(output, "window-embedded-header-breadcrumb.png"));
            model.OpenPageCommand.Execute("personalization/background"); Pump();
            var backgroundPage = view.GetVisualDescendants()
                .OfType<RelaxKonOS.App.Settings.Views.PersonalizationBackgroundView>().Single();
            var wallpaperList = backgroundPage.GetVisualDescendants().OfType<ListBox>().Single();
            Check(wallpaperList.Items.Count == settings.Wallpapers.Count
                && wallpaperList.SelectedIndex == settings.WallpaperIndex,
                "The background page lost the wallpaper choices or the current selection.");
            model.OpenPageCommand.Execute("personalization/layout"); Pump();
            var layoutPage = view.GetVisualDescendants()
                .OfType<RelaxKonOS.App.Settings.Views.PersonalizationLayoutView>().Single();
            var layoutPicker = layoutPage.GetVisualDescendants().OfType<ComboBox>().Single();
            Check(Equals(layoutPicker.SelectedValue, settings.SelectedShellId),
                "The layout page lost the selected shell ID.");
            model.OpenPageCommand.Execute("personalization/style"); Pump();
            var stylePage = view.GetVisualDescendants()
                .OfType<RelaxKonOS.App.Settings.Views.PersonalizationStyleView>().Single();
            var stylePicker = stylePage.GetVisualDescendants().OfType<ComboBox>().Single();
            Check(Equals(stylePicker.SelectedValue, settings.SystemStyleId),
                "The system style page lost the selected style ID.");
            model.OpenPageCommand.Execute("developer"); Pump();
            var developerPage = view.GetVisualDescendants()
                .OfType<RelaxKonOS.App.Settings.Views.DeveloperPageView>().Single();
            Check(developerPage.GetVisualDescendants().OfType<TextBox>()
                    .Any(box => box.IsReadOnly && box.Text == developer.Endpoint),
                "The developer page failed to show the local endpoint as read-only.");
            model.OpenPageCommand.Execute("account-security"); Pump();
            var accountPage = view.GetVisualDescendants()
                .OfType<RelaxKonOS.App.Settings.Views.AccountSecurityPageView>().Single();
            Check(RelaxKonOS.App.Settings.Views.SettingsSearchTarget.Find(accountPage, "account.alias") is not null,
                "The account security page lost its alias search target.");

            // The same window under every window-chrome recipe. The Settings view names no column and
            // no inset, so this is the assertion that the host's role slots really do the placing -
            // and it is the only place the fused header is ever seen on the left-button recipes.
            foreach (var styleId in new[] { SystemStyleIds.MacOsLike, SystemStyleIds.UbuntuLike, SystemStyleIds.WindowsLike })
            {
                settings.SystemStyleId = styleId; Pump();
                BuiltInSystemStyles.TryGet(styleId, out var profile);
                var recipe = profile.SupportedRecipes.WindowChrome;
                Check((string)managed.View.FindResource("SystemStyle.WindowChrome")! == recipe,
                    $"{styleId} selected {recipe} but the window did not adopt it.");
                var bar = managed.View.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_TitleBar");
                var cluster = managed.View.GetVisualDescendants().OfType<Grid>().Single(grid => grid.Name == "PART_WindowControls");
                var expectedFused = Math.Max(48, profile.ResolveTokens(dark: false)["WindowCaptionHeight"] + 14);
                Check(Math.Abs(bar.Bounds.Height - expectedFused) < 1,
                    $"{styleId}: fused bar kept a single shared height ({bar.Bounds.Height}, expected {expectedFused}).");
                Check(!Overlaps(view.Header, cluster, managed.View) && !Overlaps(view.SearchBar, cluster, managed.View),
                    $"{styleId}: the application's title-bar blocks overlap the window controls.");
                Check(recipe != "traffic-lights-left" || LeftEdge(view.Header, managed.View) >= RightEdge(cluster, managed.View) - 1,
                    $"{styleId}: the leading block starts before the leading caption cluster ends.");
                Check(view.SearchBar.SearchBox.Bounds.Width >= 80 && view.SearchBar.IsEffectivelyVisible,
                    $"{styleId}: the title-bar search field collapsed.");
                Capture(window, new PixelSize(1100, 800), Path.Combine(output, $"window-embedded-header-{recipe}.png"));
            }
            settings.SystemStyleId = SystemStyleIds.WindowsLike; Pump();
            var networkEditor = model.Pages.OfType<NetworkPageViewModel>().Single().HostNetwork;
            var adapter = new NetworkAdapterItem(new HostNetworkAdapter(Guid.NewGuid().ToString(), 2,
                "Ethernet 2", "Remote network adapter", "ethernet", true, 1_000_000_000, "00:11:22:33:44:55",
                true, true, [new("192.168.1.5", 24)], ["192.168.1.1"], ["192.168.1.1"], "r1", true));
            networkEditor.Adapters.Add(adapter);
            model.OpenPageCommand.Execute("network");
            networkEditor.OpenAdapter(adapter); Pump();
            Check(model.SelectedPage?.Route == "network/adapter" && model.ParentRoute == "network"
                && model.SelectedCategory?.Route == "network", "Adapter card did not open a nested network route.");
            Check(view.FindControl<TextBlock>("PageHeading")!.Text == adapter.Name, "Adapter breadcrumb lost its name.");
            Capture(window, new PixelSize(1100, 800), Path.Combine(output, "window-embedded-network-adapter.png"));
            view.FindControl<TextBlock>("PageHeading")!.Focus();
            foreach (var mode in new[] { RelaxKonOS.Protocol.Desktop.ThemeKind.Light, RelaxKonOS.Protocol.Desktop.ThemeKind.Dark })
            {
                settings.Appearance = settings.Appearance with { Mode = mode }; Pump();
                Capture(window, new PixelSize(1100, 800), Path.Combine(output, $"window-embedded-network-adapter-{mode}.png"));
            }
            Click(window, view.FindControl<Button>("ParentBreadcrumb")!);
            Check(model.SelectedPage?.Route == "network", "Network breadcrumb did not return to adapter list.");
            model.BackCommand.Execute(null); Pump();
            Check(model.SelectedPage?.Route == "network/adapter", "Back history lost the adapter detail route.");
            Click(window, view.FindControl<Button>("ParentBreadcrumb")!);
            var secondAdapter = new NetworkAdapterItem(adapter.Value with { Id = Guid.NewGuid().ToString(), Name = "Ethernet 3" });
            networkEditor.Adapters.Add(secondAdapter);
            networkEditor.OpenAdapter(secondAdapter); Pump();
            model.BackCommand.Execute(null); Pump();
            model.BackCommand.Execute(null); Pump();
            Check(model.SelectedPage?.Route == "network/adapter" && networkEditor.SelectedAdapter == adapter,
                "Back history restored a different adapter than the recorded detail page.");
            apps.RegisterBuiltIn(new BreadcrumbApplication("relaxkonos.taskmanager", "Task Manager"));
            apps.RegisterBuiltIn(new BreadcrumbApplication("relaxkonos.breadcrumb-test", "Second application")); Pump();
            var defaultAppsPage = model.Pages.OfType<DefaultAppsPageViewModel>().Single();
            var originalMappings = defaultAppsPage.ToMappings();
            defaultAppsPage.SetMappings([new(".custom-fixture", "relaxkonos.breadcrumb-test")]);
            model.OpenPageCommand.Execute("default-apps"); Pump();
            Check(view.GetVisualDescendants().OfType<AutoCompleteBox>().Any(input =>
                    input.Text == ".custom-fixture" && input.ItemsSource!.Cast<string>().Contains(".custom-fixture")),
                "The default apps page lost the persisted custom extension or its autocomplete entry.");
            defaultAppsPage.SetMappings(originalMappings);
            var appsEditor = model.Pages.OfType<AppsPageViewModel>().Single();
            var permissionPage = model.Pages.OfType<ApplicationPermissionsPageViewModel>().Single();
            var permissionStore = DispatchProxy.Create<IAppPermissionManager, BreadcrumbPermissions>();
            permissionPage.CreateEditor = (app, complete) => new AppPermissionDialogViewModel(app, permissionStore, localization, complete);
            var firstApp = appsEditor.RegisteredApps.Single(entry => entry.Id.Value == "relaxkonos.taskmanager").App;
            var secondApp = appsEditor.RegisteredApps.Single(entry => entry.Id.Value == "relaxkonos.breadcrumb-test").App;
            model.OpenPageCommand.Execute("apps"); Pump();
            var appButton = view.GetVisualDescendants().OfType<Button>()
                .Single(button => ReferenceEquals(button.CommandParameter, firstApp));
            Click(window, appButton); Pump();
            Check(model.SelectedPage?.Route == "apps/detail" && model.ParentRoute == "apps"
                && model.SelectedCategory?.Route == "apps", "Application information is not a nested apps route.");
            Check(view.FindControl<TextBlock>("PageHeading")!.Text == appsEditor.SelectedApp!.DisplayName, "Application breadcrumb lost its name.");
            appsEditor.EditSelectedPermissionsCommand.ExecuteAsync(null).GetAwaiter().GetResult(); Pump();
            Check(model.SelectedPage?.Route == "apps/permissions" && model.HasGrandparentPage
                && model.ParentTitle == appsEditor.SelectedApp!.DisplayName, "Permissions have no three-level breadcrumb.");
            var permission = permissionPage.Editor!.PermissionGroups.SelectMany(group => group.Permissions).First();
            permission.IsGranted = true;
            Check(permissionStore.GetStatus(firstApp.Id, permission.PermissionId) == RelaxKonOS.AppSDK.AppPermissionStatus.Undecided, "Navigating permission drafts granted access.");
            Click(window, view.FindControl<Button>("ParentBreadcrumb")!);
            Check(model.SelectedPage?.Route == "apps/detail", "Permission parent did not return to application information.");
            appsEditor.EditSelectedPermissionsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(permissionPage.Editor!.PermissionGroups.SelectMany(group => group.Permissions).First().IsGranted, "Same-app navigation discarded permission draft.");
            permissionPage.Editor.SaveCommand.Execute(null); Pump();
            Check(model.SelectedPage?.Route == "apps/detail" && permissionStore.GetStatus(firstApp.Id, permission.PermissionId) == RelaxKonOS.AppSDK.AppPermissionStatus.Granted,
                "Permission save lost application context.");
            appsEditor.EditSelectedPermissionsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            permissionPage.Editor!.PermissionGroups.SelectMany(group => group.Permissions).First().IsGranted = false;
            permissionPage.Editor.CancelCommand.Execute(null); Pump();
            Check(permissionStore.GetStatus(firstApp.Id, permission.PermissionId) == RelaxKonOS.AppSDK.AppPermissionStatus.Granted, "Permission cancel persisted a draft.");
            Click(window, view.FindControl<Button>("ParentBreadcrumb")!);
            appsEditor.ShowAppDetailsCommand.Execute(secondApp); Pump();
            model.BackCommand.Execute(null); Pump(); model.BackCommand.Execute(null); Pump();
            Check(model.SelectedPage?.Route == "apps/detail" && appsEditor.SelectedApp?.Id == firstApp.Id, "Back history restored a different application.");
            settings.Appearance = settings.Appearance with { Mode = RelaxKonOS.Protocol.Desktop.ThemeKind.Light };
            foreach (var culture in new[] { "zh-CN", "en-US", "ja-JP" })
            {
                settings.Language = culture; Pump();
                Capture(window, new PixelSize(1100, 800), Path.Combine(output, $"window-app-details-{culture}.png"));
                appsEditor.EditSelectedPermissionsCommand.ExecuteAsync(null).GetAwaiter().GetResult(); Pump();
                Capture(window, new PixelSize(1100, 800), Path.Combine(output, $"window-app-permissions-{culture}.png"));
                managed.View.Width = 320; managed.View.Height = 480; Pump();
                Check(scroll.Extent.Width <= scroll.Viewport.Width + 1, "Application permissions require horizontal scrolling in narrow layout.");
                Capture(window, new PixelSize(1100, 800), Path.Combine(output, $"window-app-permissions-narrow-{culture}.png"));
                managed.View.Width = 1080; managed.View.Height = 780; Pump();
                Click(window, view.FindControl<Button>("GrandparentBreadcrumb")!);
                Check(model.SelectedPage?.Route == "apps", "Applications ancestor did not return to the list.");
                appsEditor.ShowAppDetailsCommand.Execute(appsEditor.RegisteredApps.Single(entry => entry.Id == firstApp.Id).App); Pump();
            }
            settings.Language = "zh-CN"; Pump();
            model.SelectApplicationPermissionsAsync(firstApp.Id.Value).GetAwaiter().GetResult(); Pump();
            Check(model.SelectedPage?.Route == "apps/permissions" && appsEditor.SelectedApp?.Id == firstApp.Id,
                "Host permission activation did not select the application permission route.");
            model.SelectApplicationPermissionsAsync("missing.application").GetAwaiter().GetResult(); Pump();
            Check(model.SelectedPage?.Route == "apps" && appsEditor.IsInstalledApps,
                "Missing application activation left old application details inside the list page.");
            var dragPoint = managed.View.TranslatePoint(new Point(850, 28), window)!.Value;
            oldBounds = managed.Info.Bounds;
            window.MouseDown(dragPoint, MouseButton.Left);
            window.MouseMove(dragPoint + new Vector(12, 10));
            window.MouseUp(dragPoint + new Vector(12, 10), MouseButton.Left); Pump();
            Check(managed.Info.Bounds != oldBounds, "Empty custom title-bar space cannot drag the window.");
            var closeCaption = managed.View.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PART_Close");
            Click(window, closeCaption);
            Check(!windows.Windows.Contains(managed), "Host-owned close caption stopped working.");
            if (packageSource is not null && componentCenter)
            {
                auth.State = AuthSessionState.Authenticated;
                auth.User = new(Guid.NewGuid(), "fixture", RelaxKonOS.Protocol.Common.HostPlatformKind.Linux,
                    "fixture", DateTimeOffset.UtcNow, null);
                using var centerServices = new ServiceCollection()
                    .AddSingleton<RelaxKonOS.WindowManager.IWindowManager>(windows)
                    .AddSingleton(session)
                    .AddSingleton(DispatchProxy.Create<IRuntimeCatalogClient, DisplayOnlyRuntime>())
                    .AddSingleton(new ComponentDraftStore())
                    .AddSingleton(apps)
                    .BuildServiceProvider();
                var centerWorkspace = new ComponentCenterHostWorkspace(centerServices);
                CheckCenterPackage(packageSource, windows, apps,
                    CreateFactory(new RelaxKonOS.Client.Apps.Settings.SettingsWindow(services), centerWorkspace));
            }
            else if (packageSource is not null)
            {
                auth.Logout();
                Pump();
                var ssh = new RelaxKonOS.Client.Services.ServerCenter.SshDesktopSession(null!, settings);
                CheckInstalledPackage(packageSource, windows, apps, workspace => CreateFactory(workspace, null), new ServiceCollection()
                    .AddSingleton<RelaxKonOS.WindowManager.IWindowManager>(windows)
                    .AddSingleton(settings)
                    .AddSingleton(session)
                    .AddSingleton<IWorkspaceSettingsService>(preferenceService)
                    .AddSingleton(apps)
                    .AddSingleton(DispatchProxy.Create<IRelaxKonOSClient, UnusedPreviewServices>())
                    .AddSingleton(DispatchProxy.Create<ITaskManagerClient, UnusedPreviewServices>())
                    .AddSingleton(registry)
                    .AddSingleton<IAppPermissionManager>(new RoappCatalogChecks.Permissions())
                    .AddSingleton(DispatchProxy.Create<IAppDataManager, UnusedPreviewServices>())
                    .AddSingleton(localization)
                    .AddSingleton(developer)
                    .AddSingleton(inspector)
                    .AddSingleton(new WallpaperService(session, DispatchProxy.Create<IWallpaperClient, UnusedPreviewServices>(), settings, ssh))
                    .AddSingleton(DispatchProxy.Create<IBrowserData, UnusedPreviewServices>())
                    .AddSingleton(editor)
                    .AddSingleton(ssh)
                    .AddSingleton(services.GetRequiredService<IHostTimeService>())
                    .AddSingleton(services.GetRequiredService<IHostIdentityService>())
                    .AddSingleton(services.GetRequiredService<IHostNetworkService>())
                    .AddSingleton(DispatchProxy.Create<IHostEnvironmentService, UnusedPreviewServices>()));
            }
            Console.WriteLine("PASS: Full Settings window drawer, Escape/Ctrl+F, search/back/scroll, save failure/retry/durability/source reset, three languages and magnified access.");
        }
        finally
        {
            window.Close();
            typeof(RelaxKonOS.Client.App).GetProperty(nameof(RelaxKonOS.Client.App.Services))!.SetValue(null, previousServices);
            settings.Apply(original); Pump();
        }
    }

    private static void CheckCenterPackage(string source, RelaxKonOS.WindowManager.WindowManager windows,
        ApplicationManager applications, ExternalAppContextFactory factory)
    {
        var drive = new RelaxKonOS.Client.Services.VirtualSystemDrive.VirtualSystemDrive(
            Path.Combine(Path.GetTempPath(), "rk-center-package-" + Guid.NewGuid().ToString("N")));
        var packages = new DeveloperPackageManager(applications, factory, windows, new RoappCatalogChecks.Permissions(),
            new RoappCatalogChecks.Diagnostics(), drive, source);
        const string id = "relaxkonos.component-center";
        var path = Path.Combine(source, id + ".roapp");
        var install = InstallAsync();
        PumpUntil(() => install.IsCompleted);
        install.GetAwaiter().GetResult();
        try
        {
            var firstUri = ComponentCenterNavigation.Component("test-service", "relaxkonos.proxy-server", "sing-box", ComponentCenterPage.Updates);
            var first = applications.Activate(new AppActivationRequest(firstUri));
            Check(first.Status == AppActivationStatus.Activated, "The installed Component Center did not claim its route.");
            PumpUntil(() => windows.Windows.Count > 0);
            var window = windows.Windows.Single();
            var workspace = window.View.Content as ComponentCenterWorkspace;
            var model = workspace?.DataContext as ComponentCenterViewModel;
            Check(window.Info.OwnerAppId.Value == id && model?.SelectedPage == ComponentCenterPage.Updates,
                "The installed package did not open the production workspace and dispatch navigation.");
            var controls = workspace!.GetLogicalDescendants().ToArray();
            Check(controls.OfType<ComponentCenterHeaderActions>().Any()
                && controls.OfType<ComponentCenterNavigationView>().Any()
                && controls.OfType<ComponentCenterLayout>().Any()
                && controls.OfType<ComponentIdentityView>().Any()
                && controls.OfType<ComponentDesktopPackageView>().Any()
                && controls.OfType<ComponentGitInstallationView>().Any()
                && controls.OfType<ComponentOfficialDownloadsView>().Any()
                && controls.OfType<ComponentPackageCandidateView>().Any()
                && controls.OfType<CatalogSourcesView>().Any()
                && controls.OfType<ServerModuleSecurityView>().Any(),
                "The installed package workspace is missing a migrated Component Center view.");
            var secondUri = ComponentCenterNavigation.Component("test-service", "relaxkonos.proxy-server", "sing-box", ComponentCenterPage.Installed);
            var second = applications.Activate(new AppActivationRequest(secondUri));
            Pump();
            Check(second.Status == AppActivationStatus.Activated && windows.Windows.Single() == window
                && model?.SelectedPage == ComponentCenterPage.Installed, "Component Center deep links did not reuse the package window.");
            var rejected = applications.Activate(new AppActivationRequest(new Uri(firstUri + "&action=install")));
            Check(rejected.Status == AppActivationStatus.RouteNotFound && model?.SelectedPage == ComponentCenterPage.Installed,
                "A route with an action parameter reached the Component Center workspace.");
            Console.WriteLine("PASS: installed Component Center opens its production workspace and dispatches validated deep links.");
        }
        finally
        {
            var remove = packages.UninstallAsync(id);
            PumpUntil(() => remove.IsCompleted);
            remove.GetAwaiter().GetResult();
        }

        async Task InstallAsync()
        {
            var (_, digest) = await packages.InspectAsync(path, CancellationToken.None);
            await using var stream = File.OpenRead(path);
            await packages.InstallOfficialAsync(stream, path, id, digest!, null, CancellationToken.None);
        }
    }

    private static void CheckInstalledPackage(string source, RelaxKonOS.WindowManager.WindowManager windows,
        ApplicationManager applications, Func<RelaxKonOS.App.Settings.ISettingsWindow, ExternalAppContextFactory> createFactory,
        IServiceCollection hostServices)
    {
        var drive = new RelaxKonOS.Client.Services.VirtualSystemDrive.VirtualSystemDrive(
            Path.Combine(Path.GetTempPath(), "rk-settings-package-" + Guid.NewGuid().ToString("N")));
        var permissions = new RoappCatalogChecks.Permissions();
        var diagnostics = new RoappCatalogChecks.Diagnostics();
        DeveloperPackageManager? packages = null;
        using var host = hostServices.AddSingleton(_ => packages!)
            .AddSingleton<RelaxKonOS.App.Settings.ISettingsWindow, RelaxKonOS.Client.Apps.Settings.SettingsWindow>()
            .BuildServiceProvider();
        var factory = createFactory(host.GetRequiredService<RelaxKonOS.App.Settings.ISettingsWindow>());
        packages = new DeveloperPackageManager(applications, factory, windows, permissions, diagnostics, drive, source);
        const string id = "relaxkonos.settings";
        var path = Path.Combine(source, id + ".roapp");
        var install = InstallAsync();
        PumpUntil(() => install.IsCompleted);
        install.GetAwaiter().GetResult();
        try
        {
            var result = applications.Activate(new AppActivationRequest(new Uri("relaxkonos://settings/about")));
            Check(result.Status == AppActivationStatus.Activated, "Installed Settings rejected its About URI.");
            PumpUntil(() => windows.Windows.Count > 0);
            var opened = windows.Windows.Single();
            Check(opened.View.Content is Control { DataContext: ISettingsWorkspace workspace } content
                && workspace.SelectedPage?.Route == "about"
                && System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(content.GetType().Assembly)?.IsCollectible == true,
                "The installed package did not open its own workspace or receive the About route.");
            var about = ((SettingsViewModel)((Control)opened.View.Content!).DataContext!)
                .Pages.OfType<AboutPageViewModel>().Single().Content;
            foreach (var (document, marker) in new[]
            {
                (about.ProjectLicense, about.LicenseName),
                (about.ThirdPartyNotices, "# Third-Party Notices"),
            })
            {
                var legal = about.RequestLegalDocumentAsync!(document);
                PumpUntil(() => windows.Windows.Count == 2 || legal.IsCompleted);
                Check(windows.Windows.Count == 2 && windows.Windows.Last().View.Content is Control legalView
                    && legalView.GetVisualDescendants().OfType<TextBox>()
                        .Any(text => text.Text?.Contains(marker, StringComparison.Ordinal) == true),
                    "The installed Settings package did not render its embedded legal text.");
                windows.Close(windows.Windows.Last());
                PumpUntil(() => legal.IsCompleted);
                legal.GetAwaiter().GetResult();
            }
            var home = applications.Activate(new AppActivationRequest(new Uri("relaxkonos://settings/home")));
            Pump();
            Check(home.Status == AppActivationStatus.Activated && windows.Windows.Single() == opened
                && ((ISettingsWorkspace)((Control)opened.View.Content!).DataContext!).SelectedPage?.Route == "home",
                "A second deep link did not reuse the installed Settings window.");
            var firstWorkspace = ((Control)opened.View.Content!).DataContext;
            windows.Close(opened);
            PumpUntil(() => windows.Windows.Count == 0);
            var reopened = applications.Activate(new AppActivationRequest(new Uri("relaxkonos://settings/about")));
            Check(reopened.Status == AppActivationStatus.Activated, "Installed Settings could not reopen after closing.");
            PumpUntil(() => windows.Windows.Count == 1);
            Check(windows.Windows.Single().View.Content is Control { DataContext: ISettingsWorkspace nextWorkspace }
                && !ReferenceEquals(firstWorkspace, nextWorkspace) && nextWorkspace.SelectedPage?.Route == "about",
                "Reopening Settings did not create a fresh workspace and dispatch the deep link.");
            Console.WriteLine("PASS: installed Settings opens the production host workspace and dispatches deep links.");
        }
        finally
        {
            var remove = packages.UninstallAsync(id);
            PumpUntil(() => remove.IsCompleted);
            remove.GetAwaiter().GetResult();
        }

        async Task InstallAsync()
        {
            var (_, digest) = await packages.InspectAsync(path, CancellationToken.None);
            await using var stream = File.OpenRead(path);
            await packages.InstallOfficialAsync(stream, path, id, digest!, null, CancellationToken.None);
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

    private static double LeftEdge(Visual visual, Visual ancestor) =>
        visual.TranslatePoint(default, ancestor)!.Value.X;

    private static double RightEdge(Visual visual, Visual ancestor) =>
        LeftEdge(visual, ancestor) + visual.Bounds.Width;

    /// <summary>Horizontal overlap only: the title bar is a single row, so vertical position carries no information.</summary>
    private static bool Overlaps(Visual first, Visual second, Visual ancestor) =>
        LeftEdge(first, ancestor) < RightEdge(second, ancestor) && LeftEdge(second, ancestor) < RightEdge(first, ancestor);
}

public class SettingsWindowSession : DispatchProxy
{
    public AuthSessionState State = AuthSessionState.Unauthenticated;
    public UserDto? User { get; set; }
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
            "get_Tokens" => tokens, "get_CurrentWorkspace" => workspace, "get_CurrentSession" => session, "get_CurrentUser" => User,
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

internal sealed class BreadcrumbApplication : RemoteApplicationBase
{
    public BreadcrumbApplication(string id, string name) => Manifest = new(new(id), name, "1.0.0", "⚙",
        RequestedPermissions: [RelaxKonOS.Core.Applications.AppPermissions.ServerMetricsRead]);
    public override ApplicationManifest Manifest { get; }
    public override void Activate(RelaxKonOS.AppSDK.AppContext context) { }
}

public class BreadcrumbPermissions : DispatchProxy
{
    private readonly Dictionary<(string App, string Permission), RelaxKonOS.AppSDK.AppPermissionStatus> _decisions = new();
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var appId = ((RelaxKonOS.Core.Applications.AppId)args![0]!).Value;
        var permission = (string)args[1]!;
        var key = (appId, permission);
        if (method!.Name == "GetStatus") return _decisions.GetValueOrDefault(key, RelaxKonOS.AppSDK.AppPermissionStatus.Undecided);
        if (method.Name == "SetStatus") { _decisions[key] = (RelaxKonOS.AppSDK.AppPermissionStatus)args[2]!; return null; }
        throw new NotSupportedException(method.Name);
    }
}
