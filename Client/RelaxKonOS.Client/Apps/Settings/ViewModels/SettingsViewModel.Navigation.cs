using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.HostSettings;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

public sealed partial class SettingsViewModel
{
    private LocalizationService _navigationLocalization = null!;
    private IHostTimeService _hostCatalog = null!;
    private SettingsSearchIndex _searchIndex = new(Array.Empty<SettingsSearchEntry>());
    private IReadOnlyList<SettingDescriptor> _remoteDescriptors = Array.Empty<SettingDescriptor>();
    private CancellationTokenSource _catalogLifetime = new();
    private readonly Stack<SettingsPageViewModel> _navigationHistory = new();
    private bool _goingBack;
    private bool _navigationDisposed;

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private IReadOnlyList<SettingsSearchEntry> _searchResults = Array.Empty<SettingsSearchEntry>();
    [ObservableProperty] private string _catalogProblem = "";
    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchQuery);
    public bool HasNoResults => HasSearch && SearchResults.Count == 0;
    public string Breadcrumb => _navigationLocalization.Get("settings.title", "Settings") + " / " + SelectedPage?.LocalizedDisplayName;
    public string ConnectionSummary => _session.State == AuthSessionState.Authenticated
        ? $"{_session.ServerUrl} · {_session.CurrentUser?.Username} · {_session.CurrentWorkspace?.Name}"
        : _navigationLocalization.Get("settings.value.not_connected", "Not connected");

    private void InitializeNavigation(LocalizationService localization, IHostTimeService catalog)
    {
        _navigationLocalization = localization;
        _hostCatalog = catalog;
        _navigationLocalization.LanguageChanged += OnNavigationLanguageChanged;
        _session.StateChanged += OnNavigationSessionChanged;
        RebuildSearchIndex();
    }

    partial void OnSelectedPageChanging(SettingsPageViewModel? value)
    {
        if (!_goingBack && SelectedPage is { } current && current != value) _navigationHistory.Push(current);
    }
    partial void OnSelectedPageChanged(SettingsPageViewModel? value)
    {
        OnPropertyChanged(nameof(Breadcrumb));
        BackCommand.NotifyCanExecuteChanged();
    }
    partial void OnSearchQueryChanged(string value)
    {
        SearchResults = _searchIndex.Search(value);
        OnPropertyChanged(nameof(HasSearch)); OnPropertyChanged(nameof(HasNoResults));
    }

    private bool CanGoBack() => _navigationHistory.Count > 0;
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        _goingBack = true;
        try { SelectedPage = _navigationHistory.Pop(); SearchQuery = ""; }
        finally { _goingBack = false; BackCommand.NotifyCanExecuteChanged(); }
    }
    [RelayCommand]
    private void OpenSearchResult(SettingsSearchEntry entry)
    {
        SelectPage(entry.Route);
        SearchQuery = "";
    }

    private void RebuildSearchIndex()
    {
        string T(string key) => _navigationLocalization.Get(key, key);
        var entries = Pages.Select(page => new SettingsSearchEntry("page." + page.Route, page.Route,
            page.LocalizedDisplayName, page.LocalizedDisplayName, T("settings.search.category"), "",
            page.LocalizedDisplayName + " " + page.DisplayName + " " + page.Route)).ToList();
        var descriptors = LocalDescriptors().Concat(_remoteDescriptors).GroupBy(d => d.SettingId).Select(g => g.Last());
        foreach (var descriptor in descriptors)
        {
            var page = Pages.FirstOrDefault(page => descriptor.Route == "relaxkonos://settings/" + page.Route);
            if (page is null) continue; // Discovery cannot turn into arbitrary URI activation.
            var title = T(descriptor.TitleKey);
            var scope = T("settings.scope." + descriptor.Scope.ToString().ToLowerInvariant());
            var reason = descriptor.Capability.ReasonCode is { } code ? T(code) : "";
            entries.Add(new(descriptor.SettingId, page.Route, title, page.LocalizedDisplayName, scope, reason,
                string.Join(' ', title, page.LocalizedDisplayName, scope, descriptor.SettingId, T(descriptor.DescriptionKey), string.Join(' ', descriptor.Keywords))));
        }
        _searchIndex = new(entries);
        OnSearchQueryChanged(SearchQuery);
    }

    private static IEnumerable<SettingDescriptor> LocalDescriptors()
    {
        (string Id, string Page, string Title, SettingsScope Scope, string Keywords)[] items =
        [
            ("workspace.theme", "personalization", "settings.theme", SettingsScope.Workspace, "theme light dark 主题 外观 テーマ"),
            ("workspace.wallpaper", "personalization", "settings.wallpaper", SettingsScope.Workspace, "wallpaper background 壁纸 背景 壁紙"),
            ("workspace.shell", "personalization", "settings.shell", SettingsScope.Workspace, "desktop shell 桌面 デスクトップ"),
            ("workspace.palette", "personalization", "settings.palette", SettingsScope.Workspace, "palette color 颜色 配色 色"),
            ("workspace.accent", "personalization", "settings.accent", SettingsScope.Workspace, "accent colour 强调色 アクセント"),
            ("workspace.customTheme", "personalization", "settings.custom_theme", SettingsScope.Workspace, "import export theme 导入 导出 インポート"),
            ("workspace.timeFormat", "time-language", "settings.time.format", SettingsScope.Workspace, "clock 12h 24h 时钟 時計"),
            ("workspace.dateFormat", "time-language", "settings.date.format", SettingsScope.Workspace, "date 日期 日付"),
            ("workspace.language", "time-language", "settings.display_language", SettingsScope.Workspace, "language 中文 English 日本語 语言 言語"),
            ("workspace.region", "time-language", "settings.region_format", SettingsScope.Workspace, "region 区域 地域"),
            ("workspace.defaultApps", "default-apps", "settings.default_apps", SettingsScope.Workspace, "extension association 文件关联 拡張子"),
            ("client.developer", "developer", "settings.developer_mode", SettingsScope.ClientDevice, "debug developer bridge 调试 开发 デバッグ"),
            ("client.diagnostics", "developer", "settings.network_inspector", SettingsScope.ClientDevice, "diagnostics request network 网络 诊断 診断"),
            ("client.apps", "apps", "settings.app_information", SettingsScope.ClientDevice, "install uninstall apps 安装 卸载 アプリ"),
            ("client.permissions", "apps", "settings.app_permissions", SettingsScope.ClientDevice, "permissions 授权 权限 権限"),
            ("account.alias", "account-security", "settings.account.title", SettingsScope.HostUser, "account security alias login 账号 安全 登录别名 アカウント セキュリティ ログイン エイリアス"),
            ("host.environment", "environment", "settings.environment.title", SettingsScope.HostUser, "PATH environment 环境变量 路径 環境変数 パス"),
            ("host.time.zone", "time-language", "settings.time_zone", SettingsScope.HostMachine, "timezone time zone 时区 タイムゾーン")
        ];
        foreach (var item in items)
            yield return new(item.Id, item.Page, item.Title, item.Title, "relaxkonos://settings/" + item.Page,
                item.Scope, "navigation", new(SettingsCapabilityState.Available), SettingsEffectiveState.Immediate, [item.Keywords]);
    }

    private async Task RefreshCatalogAsync()
    {
        var lifetime = _catalogLifetime;
        try
        {
            var connection = _hostCatalog.CaptureConnection();
            var snapshot = await _hostCatalog.CatalogAsync(connection, lifetime.Token);
            if (_navigationDisposed || lifetime != _catalogLifetime) return;
            _remoteDescriptors = snapshot.Items;
            CatalogProblem = "";
            RebuildSearchIndex();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (_navigationDisposed || lifetime != _catalogLifetime) return;
            CatalogProblem = error is RelaxKonOSAuthException auth ? auth.Title : error.Message;
        }
    }

    private void OnNavigationLanguageChanged(object? sender, EventArgs args)
    {
        RebuildSearchIndex(); OnPropertyChanged(nameof(Breadcrumb)); OnPropertyChanged(nameof(ConnectionSummary));
    }
    private void OnNavigationSessionChanged(object? sender, AuthSessionStateChangedEventArgs args) => Dispatcher.UIThread.Post(() =>
    {
        if (_navigationDisposed) return;
        _catalogLifetime.Cancel(); _catalogLifetime.Dispose(); _catalogLifetime = new();
        _remoteDescriptors = Array.Empty<SettingDescriptor>(); CatalogProblem = "";
        RebuildSearchIndex(); OnPropertyChanged(nameof(ConnectionSummary));
        if (_session.State == AuthSessionState.Authenticated) _ = RefreshCatalogAsync();
    });
    private void DisposeNavigation()
    {
        _navigationDisposed = true;
        _catalogLifetime.Cancel(); _catalogLifetime.Dispose();
        _session.StateChanged -= OnNavigationSessionChanged;
        _navigationLocalization.LanguageChanged -= OnNavigationLanguageChanged;
    }
}
