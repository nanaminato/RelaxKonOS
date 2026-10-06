using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.WorkspaceSettings;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.Diagnostics;
using RelaxKonOS.Client.Apps.Browser;
using RelaxKonOS.Client.Apps.Docker;
using RelaxKonOS.Client.Apps.TaskManager;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Runtime;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>设置应用根 VM。分类导航与详情页+ 右侧内容（当前选中页）。
/// 透传编辑 <see cref="ShellSettings"/>（即时反映到桌面外壳），并由 <see cref="Save"/> 触发防抖保存到服务端
/// （<c>/workspaces/{id}/preferences</c>，与 TerminalSettings/BrowserSettings 同模式）。
/// <see cref="InitializeAsync"/> 在窗口打开后调用一次：从服务端拉取偏好应用到 ShellSettings + 填充默认程序映射。</summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ShellSettings _settings;
    private readonly IWorkspaceSettingsService _client;
    private readonly IAuthSession _session;
    private readonly ApplicationManager? _apps;
    private readonly IRelaxKonOSClient? _remote;
    private readonly ITaskManagerClient? _system;
    private readonly DefaultAppRegistry? _registry;
    private readonly DeveloperModeService? _developerMode;
    private readonly DeveloperPackageManager? _packages;
    private readonly WallpaperService? _wallpapers;
    private readonly WorkspacePreferencesEditor _editor;
    private bool _initialized;
    private bool _disposed;

    public SettingsViewModel(
        ShellSettings settings,
        IWorkspaceSettingsService client,
        IAuthSession session,
        WorkspacePreferencesEditor editor,
        ApplicationManager? apps,
        IRelaxKonOSClient? remote,
        ITaskManagerClient? system,
        DefaultAppRegistry? registry,
        DeveloperModeService? developerMode,
        DeveloperPackageManager? packages,
        IBrowserClient? browserClient,
        NetworkInspectorWindowService? networkInspector = null,
        LocalizationService? localization = null,
        WallpaperService? wallpapers = null)
    {
        _settings = settings;
        _client = client;
        _session = session;
        _editor = editor;
        _editor.PropertyChanged += OnEditorChanged;
        _apps = apps;
        _remote = remote;
        _system = system;
        _registry = registry;
        _developerMode = developerMode;
        _packages = packages;
        _wallpapers = wallpapers;
        localization ??= App.Services.GetRequiredService<LocalizationService>();

        var save = (Action)Save;
        _devicePreferences = App.Services.GetRequiredService<DesktopDevicePreferences>();
        _devicePreferences.Changed += OnDevicePreferencesChanged;
        _devicePreferences.PropertyChanged += OnDeviceSaveChanged;
        var hostNetwork = new HostNetworkEditorViewModel(App.Services.GetRequiredService<Services.HostSettings.IHostNetworkService>(), session, localization);
        hostNetwork.RequestOpenAdapter = () => OpenPageCommand.Execute("network/adapter/" + Uri.EscapeDataString(hostNetwork.SelectedAdapter!.Value.Id));
        Pages = new SettingsPageViewModel[]
        {
            new SystemPageViewModel(settings, session, save,
                new HostIdentityEditorViewModel(App.Services.GetRequiredService<Services.HostSettings.IHostIdentityService>(), session, localization)),
            new AccountSecurityPageViewModel(settings, App.Services.GetRequiredService<AccountSecurityClient>(), session,
                App.Services.GetRequiredService<IRememberedSessionStore>(),
                App.Services.GetRequiredService<IOwnerDevicePairingEndpointStore>()),
            new PersonalizationPageViewModel(settings, save),
            new TimeLanguagePageViewModel(settings, localization, save,
                new HostTimeEditorViewModel(App.Services.GetRequiredService<Services.HostSettings.IHostTimeService>(), session, localization)),
            new NetworkPageViewModel(settings, session, remote!, App.Services.GetRequiredService<IRemoteDockerClient>(), save,
                hostNetwork),
            new NetworkAdapterPageViewModel(settings, hostNetwork),
            new AppsPageViewModel(settings, apps!, packages!, localization, browserClient!),
            new DefaultAppsPageViewModel(settings, apps!, save),
            new DeveloperPageViewModel(settings, developerMode!, networkInspector!, localization, save),
            new AboutPageViewModel(settings),
            new AccessibilityPageViewModel(settings, _devicePreferences),
            new DailySettingsPageViewModel(settings, _devicePreferences),
        };
        var appsEditor = Pages.OfType<AppsPageViewModel>().Single();
        appsEditor.PropertyChanged += OnSelectedApplicationChanged;
        var appDetails = new ApplicationDetailPageViewModel(settings, appsEditor);
        var appPermissions = new ApplicationPermissionsPageViewModel(settings, appsEditor);
        Pages = Pages.Concat(new SettingsPageViewModel[] { appDetails, appPermissions }).ToArray();
        appsEditor.RequestDetailsNavigation = id => OpenPageCommand.Execute("apps/detail/" + Uri.EscapeDataString(id));
        appsEditor.RequestApplicationsNavigation = () => { if (SelectedPage?.Route != "apps") OpenPageCommand.Execute("apps"); };
        appsEditor.RequestPermissionEditorAsync = app => { OpenPageCommand.Execute("apps/permissions/" + Uri.EscapeDataString(app.Id.Value)); return Task.CompletedTask; };
        appPermissions.ReturnToApplication = () => OpenPageCommand.Execute("apps/detail/" + Uri.EscapeDataString(appsEditor.SelectedApp!.Id.Value));
        var personalization = Pages.OfType<PersonalizationPageViewModel>().Single();
        Pages = Pages.Concat(new SettingsPageViewModel[]
        {
            new PersonalizationColorsPageViewModel(settings, personalization),
            new PersonalizationStylePageViewModel(settings, personalization),
            new PersonalizationLayoutPageViewModel(settings, personalization),
            new PersonalizationBackgroundPageViewModel(settings, personalization)
        }).ToArray();
        Pages = new SettingsPageViewModel[] { new SettingsHomePageViewModel(settings, Pages, _devicePreferences) }.Concat(Pages).ToArray();
        NavigationPages = new[] { "home", "system", "network", "personalization", "apps", "account-security", "time-language", "accessibility", "developer" }
            .Select(route => Pages.Single(page => page.Route == route)).ToArray();
        _selectedPage = Pages[0];
        InitializeNavigation(localization, App.Services.GetRequiredService<Services.HostSettings.IHostTimeService>());
        Pages.OfType<DefaultAppsPageViewModel>().Single().SetMappings(registry?.Snapshot);
        if (_registry is not null) _registry.Changed += OnMappingsChanged;
    }

    private readonly DesktopDevicePreferences _devicePreferences;
    public bool CanPinCurrentPage => SelectedPage is { } page && DesktopDevicePreferences.PinnableRoutes.Contains(page.Route);
    public bool IsCurrentPagePinned
    {
        get => SelectedPage is { } page && _devicePreferences.Value.PinnedSettings.Contains(page.Route);
        set
        {
            if (!CanPinCurrentPage || SelectedPage is not { } page) return;
            _devicePreferences.Update(p => p with { PinnedSettings = value
                ? p.PinnedSettings.Append(page.Route).Distinct().ToArray()
                : p.PinnedSettings.Where(route => route != page.Route).ToArray() });
        }
    }
    public bool HasDeviceSaveFailure => _devicePreferences.SaveFailed && SelectedPage is not DeviceSettingsPageViewModel;
    [RelayCommand] private void RetryDeviceSave() => _devicePreferences.Save();
    private void OnDeviceSaveChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => OnPropertyChanged(nameof(HasDeviceSaveFailure));
    private void OnSelectedApplicationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (_disposed || args.PropertyName != nameof(AppsPageViewModel.SelectedApp)) return;
        if (sender is AppsPageViewModel { SelectedApp: null } && SelectedPage?.Route is "apps/detail" or "apps/permissions")
        {
            Pages.OfType<ApplicationPermissionsPageViewModel>().Single().ClearEditor();
            var previous = _goingBack;
            _goingBack = true;
            try { SelectPage("apps"); } finally { _goingBack = previous; }
            return;
        }
        OnPropertyChanged(nameof(ParentTitle));
        OnPropertyChanged(nameof(CurrentNavigationRoute));
    }
    private void OnDevicePreferencesChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(IsCurrentPagePinned));

    public ShellSettings Settings => _settings;
    public IReadOnlyList<SettingsPageViewModel> Pages { get; }

    public IReadOnlyList<SettingsPageViewModel> NavigationPages { get; }
    public SettingsPageViewModel? SelectedCategory
    {
        get => Pages.FirstOrDefault(page => page.Route == (SelectedPage is PersonalizationDetailPageViewModel ? "personalization" : SelectedPage?.Route == "default-apps" ? "apps" : SelectedPage?.Route == "system/preferences" ? "system" : SelectedPage?.Route == "network/adapter" ? "network" : SelectedPage?.Route is "apps/detail" or "apps/permissions" ? "apps" : SelectedPage?.Route));
        set { if (value is not null && value != SelectedCategory) SelectPage(value.Route); }
    }
    public bool IsAppsCategory => SelectedPage?.Route == "apps";
    public string? ParentRoute => SelectedPage is PersonalizationDetailPageViewModel ? "personalization" : SelectedPage?.Route == "default-apps" ? "apps" : SelectedPage?.Route == "system/preferences" ? "system" : SelectedPage?.Route == "network/adapter" ? "network" : SelectedPage?.Route == "apps/detail" ? "apps" : SelectedPage?.Route == "apps/permissions" ? "apps/detail/" + Uri.EscapeDataString(Pages.OfType<AppsPageViewModel>().Single().SelectedApp!.Id.Value) : null;
    public bool HasParentPage => ParentRoute is not null;
    public string ParentTitle => SelectedPage?.Route == "apps/permissions" ? Pages.OfType<AppsPageViewModel>().Single().SelectedApp?.DisplayName ?? "" : Pages.FirstOrDefault(page => page.Route == ParentRoute)?.LocalizedDisplayName ?? "";
    public bool HasGrandparentPage => SelectedPage?.Route == "apps/permissions";
    public string GrandparentTitle => Pages.Single(page => page.Route == "apps").LocalizedDisplayName;
    [RelayCommand] private void OpenPage(string route) { SelectPage(route); SearchQuery = ""; }

    [ObservableProperty] private SettingsPageViewModel? _selectedPage;

    public void SelectPage(string route)
    {
        if (route.StartsWith("network/adapter/", StringComparison.OrdinalIgnoreCase))
        {
            var editor = Pages.OfType<NetworkPageViewModel>().Single().HostNetwork;
            var id = Uri.UnescapeDataString(route["network/adapter/".Length..]);
            var adapter = editor.Adapters.FirstOrDefault(item => item.Value.Id == id);
            if (adapter is null) return;
            if (editor.HasPendingConfirmation && adapter != editor.SelectedAdapter) return;
            var samePage = SelectedPage?.Route == "network/adapter";
            if (samePage && adapter != editor.SelectedAdapter)
            {
                if (!_goingBack) _navigationHistory.Remember(CurrentNavigationRoute, SearchQuery);
                OnPropertyChanging(nameof(SelectedPage));
            }
            if (!editor.SelectAdapterForNavigation(adapter)) return;
            route = "network/adapter";
            if (samePage) { OnPropertyChanged(nameof(SelectedPage)); BackCommand.NotifyCanExecuteChanged(); }
        }
        if (route.StartsWith("apps/detail/", StringComparison.OrdinalIgnoreCase) || route.StartsWith("apps/permissions/", StringComparison.OrdinalIgnoreCase))
        {
            var permissionsPage = route.StartsWith("apps/permissions/", StringComparison.OrdinalIgnoreCase);
            var pageRoute = permissionsPage ? "apps/permissions" : "apps/detail";
            var id = Uri.UnescapeDataString(route[(pageRoute.Length + 1)..]);
            var apps = Pages.OfType<AppsPageViewModel>().Single();
            if (!apps.RegisteredApps.Any(entry => entry.Id.Value.Equals(id, StringComparison.OrdinalIgnoreCase))) return;
            if (SelectedPage?.Route == pageRoute && apps.SelectedApp?.Id.Value != id)
            {
                if (!_goingBack) _navigationHistory.Remember(CurrentNavigationRoute, SearchQuery);
                OnPropertyChanging(nameof(SelectedPage));
            }
            if (!apps.SelectApplication(id)) return;
            if (permissionsPage) Pages.OfType<ApplicationPermissionsPageViewModel>().Single().Open();
            route = pageRoute;
            if (SelectedPage?.Route == pageRoute) { OnPropertyChanged(nameof(SelectedPage)); BackCommand.NotifyCanExecuteChanged(); }
        }
        if (route == "apps") Pages.OfType<AppsPageViewModel>().Single().Subpage = AppsSubpage.InstalledApps;
        var page = Pages.FirstOrDefault(page => string.Equals(page.Route, route, StringComparison.OrdinalIgnoreCase));
        if (page is not null) SelectedPage = page;
    }

    /// <summary>Host activation entry point for a specific application's permission editor.</summary>
    public Task SelectApplicationPermissionsAsync(string appId)
    {
        var page = Pages.OfType<AppsPageViewModel>().FirstOrDefault();
        if (page is null) return Task.CompletedTask;
        SelectPage("apps");
        return page.OpenPermissionsAsync(appId);
    }

    /// <summary>窗口打开后调用：加载服务端偏好并应用到 ShellSettings + 默认程序映射。</summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        _ = RefreshCatalogAsync();
        _ = Pages.OfType<AccountSecurityPageViewModel>().Single().LoadAsync();

        if (_session is not { State: AuthSessionState.Authenticated, ServiceId: { } serviceId, EffectiveBaseUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } ws })
            return;

        if (Pages.OfType<NetworkPageViewModel>().FirstOrDefault() is { } networkPage)
        {
            await networkPage.HostNetwork.ReloadAsync();
            await networkPage.LoadOutboundProxyAsync();
        }
        try
        {
            if (_editor.HasDraft) return;
            var readId = Guid.NewGuid();
            LanguageSwitchDiagnostics.Record("settings.initialize.read", new { readId, language = _settings.Language });
            var prefs = await _client.GetAsync(url, tokens.AccessToken, ws.Id);
            LanguageSwitchDiagnostics.Record("settings.initialize.received", new { readId, incoming = prefs.Language, prefs.Revision, hasDraft = _editor.HasDraft });
            if (_editor.HasDraft || _session.ServiceId != serviceId || _session.CurrentWorkspace?.Id != ws.Id || _session.Tokens?.AccessToken != tokens.AccessToken) return;
            LanguageSwitchDiagnostics.Record("settings.initialize.apply", new { readId, incoming = prefs.Language, actual = _settings.Language });
            if (_wallpapers is not null)
                await _wallpapers.ApplyAsync(prefs);
            else
                _settings.Apply(prefs);
            if (Pages.OfType<DefaultAppsPageViewModel>().FirstOrDefault() is { } defaultAppsPage)
                defaultAppsPage.SetMappings(prefs.DefaultApps);
        }
        catch
        {
            // Keep the last local snapshot. A missing revision cannot be submitted as a successful write.
        }
    }

    /// <summary>
    /// Save-status line. The idle state intentionally shows nothing, so it is answered here rather
    /// than through the resource table: <c>LocalizedText.Get</c> uses the key as its own fallback,
    /// which would surface the literal text "settings.save.idle" for an empty translation.
    /// </summary>
    public string SaveStatus => _editor.State is PreferencesSaveState.Idle or PreferencesSaveState.Saving or PreferencesSaveState.Accepted or PreferencesSaveState.Saved
        ? string.Empty
        : LocalizedText.Get("settings.save." + (_editor.State == PreferencesSaveState.Failed
            && _editor.Failure != PreferencesSaveFailure.Unexpected
                ? "failed_" + _editor.Failure.ToString().ToLowerInvariant()
                : _editor.State.ToString().ToLowerInvariant()));
    public bool CanDiscard => _editor.HasDraft && _editor.State != PreferencesSaveState.Saving;
    public bool CanRetry => _editor.State == PreferencesSaveState.Failed;

    public string? SaveSourceRoute => _editor.Source is { ApplicationId: "relaxkonos.settings" } source
        && Pages.Any(page => page.Route == source.PageRoute) ? source.PageRoute : null;
    public bool HasLocalSaveFeedback => SaveStatus.Length > 0 && SaveSourceRoute is { } route && route == SelectedPage?.Route;
    public bool HasOtherSaveFeedback => SaveStatus.Length > 0 && !HasLocalSaveFeedback && _editor.State != PreferencesSaveState.Saved;
    public bool CanOpenSaveSource => HasOtherSaveFeedback && SaveSourceRoute is not null;
    public string SaveSummary => SaveSourceRoute is { } route
        ? Pages.First(page => page.Route == route).LocalizedDisplayName + " · " + SaveStatus : SaveStatus;

    private void OnEditorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!_disposed) NotifySaveFeedback(); });
            return;
        }
        NotifySaveFeedback();
    }

    private PreferencesSaveState _lastFeedbackState;
    private readonly Avalonia.Threading.DispatcherTimer _failureToastTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    [ObservableProperty] private string _failureToast = "";
    public bool HasFailureToast => FailureToast.Length > 0;
    partial void OnFailureToastChanged(string value) => OnPropertyChanged(nameof(HasFailureToast));

    private void NotifySaveFeedback()
    {
        if (_lastFeedbackState != _editor.State)
        {
            _lastFeedbackState = _editor.State;
            DismissFailureToast(null, EventArgs.Empty);
            if (_editor.State is PreferencesSaveState.Failed or PreferencesSaveState.Conflict or PreferencesSaveState.Offline)
            {
                FailureToast = SaveStatus;
                _failureToastTimer.Stop();
                _failureToastTimer.Tick -= DismissFailureToast;
                _failureToastTimer.Tick += DismissFailureToast;
                _failureToastTimer.Start();
            }
        }
        OnPropertyChanged(nameof(SaveStatus));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanDiscard));
        OnPropertyChanged(nameof(SaveSourceRoute));
        OnPropertyChanged(nameof(HasLocalSaveFeedback));
        OnPropertyChanged(nameof(HasOtherSaveFeedback));
        OnPropertyChanged(nameof(CanOpenSaveSource));
        OnPropertyChanged(nameof(SaveSummary));
    }

    private void DismissFailureToast(object? sender, EventArgs args) { _failureToastTimer.Stop(); FailureToast = ""; }

    private void OnMappingsChanged(object? sender, EventArgs args)
    {
        if (!_editor.HasDraft)
            Pages.OfType<DefaultAppsPageViewModel>().Single().SetMappings(_registry?.Snapshot);
    }

    [RelayCommand]
    private async Task DiscardDraftAsync()
    {
        var serviceId = _session.ServiceId;
        var sessionId = _session.CurrentSession?.Id;
        var workspaceId = _session.CurrentWorkspace?.Id;
        var snapshot = await _editor.DiscardAndReloadAsync();
        if (snapshot is null || _session.ServiceId != serviceId || _session.CurrentSession?.Id != sessionId
            || _session.CurrentWorkspace?.Id != workspaceId) return;
        if (_wallpapers is not null) await _wallpapers.ApplyAsync(snapshot);
        else _settings.Apply(snapshot);
        Pages.OfType<DefaultAppsPageViewModel>().Single().SetMappings(snapshot.DefaultApps);
    }

    [RelayCommand]
    private void RetrySave() => _editor.Retry();

    /// <summary>Drafts, target binding, and debounce belong to the independent service.</summary>
    internal void Save()
    {
        if (!_initialized) return;
        var mappings = Pages.OfType<DefaultAppsPageViewModel>().FirstOrDefault()?.ToMappings() ?? Array.Empty<DefaultAppMappingDto>();
        _editor.Schedule(_settings.ToPreferences(mappings), SelectedPage is { } page ? new PreferencesEditSource("relaxkonos.settings", page.Route) : null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _failureToastTimer.Stop();
        _failureToastTimer.Tick -= DismissFailureToast;
        DisposeNavigation();
        _devicePreferences.Changed -= OnDevicePreferencesChanged;
        _devicePreferences.PropertyChanged -= OnDeviceSaveChanged;
        _editor.PropertyChanged -= OnEditorChanged;
        Pages.OfType<AppsPageViewModel>().Single().PropertyChanged -= OnSelectedApplicationChanged;
        if (_registry is not null) _registry.Changed -= OnMappingsChanged;
        foreach (var page in Pages)
            page.Dispose();
    }
}
