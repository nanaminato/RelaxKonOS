using RelaxKonOS.Client.Services.WorkspaceSettings;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.Diagnostics;
using RelaxKonOS.Client.Apps.Browser;
using RelaxKonOS.Client.Apps.TaskManager;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Runtime;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>设置应用根 VM。左侧导航（八个分类页）+ 右侧内容（当前选中页）。
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
        IImageMirrorClient? imageMirrors,
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
        Pages = new SettingsPageViewModel[]
        {
            new SystemPageViewModel(settings, session, save),
            new PersonalizationPageViewModel(settings, save),
            new TimeLanguagePageViewModel(settings, localization, save),
            new NetworkPageViewModel(settings, session, remote!, system!, save),
            new AppsPageViewModel(settings, apps!, packages!, localization, browserClient!),
            new ImageMirrorsPageViewModel(settings, imageMirrors!, session),
            new DefaultAppsPageViewModel(settings, apps!, save),
            new DeveloperPageViewModel(settings, developerMode!, networkInspector!, localization, save),
        };
        _selectedPage = Pages[0];
        Pages.OfType<DefaultAppsPageViewModel>().Single().SetMappings(registry?.Snapshot);
        if (_registry is not null) _registry.Changed += OnMappingsChanged;
    }

    public ShellSettings Settings => _settings;
    public IReadOnlyList<SettingsPageViewModel> Pages { get; }

    [ObservableProperty] private SettingsPageViewModel? _selectedPage;

    public void SelectPage(string route)
    {
        var page = route.ToLowerInvariant() switch
        {
            "system" => Pages.OfType<SystemPageViewModel>().FirstOrDefault(),
            "personalization" => Pages.OfType<PersonalizationPageViewModel>().FirstOrDefault(),
            "time-language" => Pages.OfType<TimeLanguagePageViewModel>().FirstOrDefault(),
            "network" => Pages.OfType<NetworkPageViewModel>().FirstOrDefault(),
            "apps" => Pages.OfType<AppsPageViewModel>().FirstOrDefault(),
            "image-mirrors" => Pages.OfType<ImageMirrorsPageViewModel>().FirstOrDefault(),
            "default-apps" => Pages.OfType<DefaultAppsPageViewModel>().FirstOrDefault(),
            "developer" => Pages.OfType<DeveloperPageViewModel>().FirstOrDefault(),
            _ => (SettingsPageViewModel?)null,
        };
        if (page is not null) SelectedPage = page;
    }

    /// <summary>Host activation entry point for a specific application's permission editor.</summary>
    public Task SelectApplicationPermissionsAsync(string appId)
    {
        var page = Pages.OfType<AppsPageViewModel>().FirstOrDefault();
        if (page is null) return Task.CompletedTask;
        SelectedPage = page;
        return page.OpenPermissionsAsync(appId);
    }

    /// <summary>窗口打开后调用：加载服务端偏好并应用到 ShellSettings + 默认程序映射。</summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        if (_session is not { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } ws })
            return;

        if (Pages.OfType<NetworkPageViewModel>().FirstOrDefault() is { } networkPage)
            await networkPage.LoadServerAddressesAsync();
        if (Pages.OfType<ImageMirrorsPageViewModel>().FirstOrDefault() is { } imageMirrorsPage)
            await imageMirrorsPage.LoadAsync();

        try
        {
            if (_editor.HasDraft) return;
            var prefs = await _client.GetAsync(url, tokens.AccessToken, ws.Id);
            if (_editor.HasDraft || _session.ServerUrl != url || _session.CurrentWorkspace?.Id != ws.Id || _session.Tokens?.AccessToken != tokens.AccessToken) return;
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

    public string SaveStatus => RelaxKonOS.Client.Localization.LocalizedText.Get("settings.save." + _editor.State.ToString().ToLowerInvariant());
    public bool CanDiscard => _editor.HasDraft && _editor.State != PreferencesSaveState.Saving;
    public bool CanRetry => _editor.State == PreferencesSaveState.Failed;

    private void OnEditorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        OnPropertyChanged(nameof(SaveStatus));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanDiscard));
    }

    private void OnMappingsChanged(object? sender, EventArgs args)
    {
        if (!_editor.HasDraft)
            Pages.OfType<DefaultAppsPageViewModel>().Single().SetMappings(_registry?.Snapshot);
    }

    [RelayCommand]
    private async Task DiscardDraftAsync()
    {
        var url = _session.ServerUrl;
        var sessionId = _session.CurrentSession?.Id;
        var workspaceId = _session.CurrentWorkspace?.Id;
        var snapshot = await _editor.DiscardAndReloadAsync();
        if (snapshot is null || _session.ServerUrl != url || _session.CurrentSession?.Id != sessionId
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
        _editor.Schedule(_settings.ToPreferences(mappings));
    }

    public void Dispose()
    {
        _editor.PropertyChanged -= OnEditorChanged;
        if (_registry is not null) _registry.Changed -= OnMappingsChanged;
        foreach (var page in Pages.OfType<IDisposable>())
            page.Dispose();
    }
}
