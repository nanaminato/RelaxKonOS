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
    private readonly SettingsNavigationHistory _navigationHistory = new();
    private bool _goingBack;
    internal bool IsRestoringNavigation => _goingBack;
    private bool _navigationDisposed;

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private IReadOnlyList<SettingsSearchEntry> _searchResults = Array.Empty<SettingsSearchEntry>();
    [ObservableProperty] private string _catalogProblem = "";
    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchQuery);
    public bool HasNoResults => HasSearch && SearchResults.Count == 0;
    public string Breadcrumb => _navigationLocalization.Get("settings.title", "Settings") + " / "
        + (SelectedPage is { } page ? CategoryPath(page).Replace(" › ", " / ") : "");
    public event Action<SettingsSearchEntry>? SearchResultOpened;
    public event Action? NavigationContextReset;
    [ObservableProperty] private string _searchLocationStatus = "";
    public string ConnectionAddress => _session.EffectiveBaseUrl ?? "";
    public bool HasPageScopeDescription => PageScopeDescription.Length > 0;
    public string PageScopeDescription => SelectedPage?.Route == "home" ? "" : _navigationLocalization.Get(SelectedPage?.Route switch
    {
        "personalization" or "default-apps" => "settings.scope_hint.workspace",
        string route when route.StartsWith("personalization/") => "settings.scope_hint.workspace",
        "developer" or "about" or "accessibility" or "system/preferences" => "settings.scope_hint.device",
        "time-language" => "settings.scope_hint.time",
        "system" => "settings.scope_hint.system",
        "network" => "settings.scope_hint.network",
        "account-security" => "settings.scope_hint.account",
        "apps" => "settings.scope_hint.apps",
        _ => "settings.scope_hint.home"
    }, "Check the scope of each section before changing settings.");
    public string ConnectionSummary => _session.State == AuthSessionState.Authenticated
        ? string.Format(_navigationLocalization.Get("settings.connection.summary", "Account: {0} · Workspace: {1}"), _session.CurrentUser?.Username ?? "—", _session.CurrentWorkspace?.Name ?? "—")
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
        if (!_goingBack && SelectedPage is { } current && current != value) _navigationHistory.Remember(current.Route, SearchQuery);
    }
    partial void OnSelectedPageChanged(SettingsPageViewModel? value)
    {
        SearchLocationStatus = "";
        NotifySaveFeedback();
        OnPropertyChanged(nameof(PageScopeDescription));
        OnPropertyChanged(nameof(HasPageScopeDescription));
        OnPropertyChanged(nameof(Breadcrumb));
        OnPropertyChanged(nameof(HasDeviceSaveFailure));
        OnPropertyChanged(nameof(CanPinCurrentPage));
        OnPropertyChanged(nameof(IsCurrentPagePinned));
        OnPropertyChanged(nameof(SelectedCategory));
        OnPropertyChanged(nameof(IsAppsCategory));
        OnPropertyChanged(nameof(ParentRoute));
        OnPropertyChanged(nameof(ParentTitle));
        OnPropertyChanged(nameof(HasParentPage));
        BackCommand.NotifyCanExecuteChanged();
    }
    partial void OnSearchQueryChanged(string value)
    {
        SearchResults = _searchIndex.Search(value);
        OnPropertyChanged(nameof(HasSearch)); OnPropertyChanged(nameof(HasNoResults));
    }

    private bool CanGoBack() => _navigationHistory.CanGoBack;
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        _goingBack = true;
        try
        {
            var location = _navigationHistory.Back();
            SelectPage(location.Route);
            SearchQuery = location.SearchQuery;
            SearchLocationStatus = "";
        }
        finally { _goingBack = false; BackCommand.NotifyCanExecuteChanged(); }
    }
    [RelayCommand]
    private void OpenSearchResult(SettingsSearchEntry entry)
    {
        if (!Pages.Any(page => page.Route == entry.Route)) return;
        if (SelectedPage is { } current) _navigationHistory.Remember(current.Route, SearchQuery);
        _goingBack = true;
        try { SelectPage(entry.Route); SearchQuery = ""; }
        finally { _goingBack = false; BackCommand.NotifyCanExecuteChanged(); }
        SearchResultOpened?.Invoke(entry);
    }

    private string CategoryPath(SettingsPageViewModel page) => page switch
    {
        PersonalizationDetailPageViewModel => Pages.First(parent => parent.Route == "personalization").LocalizedDisplayName + " › " + page.LocalizedDisplayName,
        _ when page.Route == "default-apps" => Pages.First(parent => parent.Route == "apps").LocalizedDisplayName + " › " + page.LocalizedDisplayName,
        _ when page.Route == "system/preferences" => Pages.First(parent => parent.Route == "system").LocalizedDisplayName + " › " + page.LocalizedDisplayName,
        _ => page.LocalizedDisplayName
    };

    private void RebuildSearchIndex()
    {
        string T(string key) => _navigationLocalization.Get(key, key);
        var entries = Pages.Select(page => new SettingsSearchEntry("page." + page.Route, page.Route,
            page.LocalizedDisplayName, CategoryPath(page), T("settings.search.category"), "",
            page.LocalizedDisplayName + " " + page.DisplayName + " " + page.Route)).ToList();
        var local = LocalDescriptors().ToDictionary(descriptor => descriptor.SettingId);
        // Keep the view's current route and title; the server remains authoritative for capabilities.
        var descriptors = local.Values.Concat(_remoteDescriptors.Select(remote => local.TryGetValue(remote.SettingId, out var view)
            ? remote with { Route = view.Route, TitleKey = view.TitleKey, Category = view.Category } : remote))
            .GroupBy(descriptor => descriptor.SettingId).Select(group => group.Last());
        foreach (var descriptor in descriptors)
        {
            var page = Pages.FirstOrDefault(page => descriptor.Route == "relaxkonos://settings/" + page.Route);
            if (page is null) continue; // Discovery cannot turn into arbitrary URI activation.
            var title = T(descriptor.TitleKey);
            var scope = descriptor.SettingId == "host.environment" ? T("settings.scope.hostenvironment") : T("settings.scope." + descriptor.Scope.ToString().ToLowerInvariant());
            var reason = descriptor.Capability.ReasonCode is { } code ? T(code) : "";
            entries.Add(new(descriptor.SettingId, page.Route, title, CategoryPath(page), scope, reason,
                string.Join(' ', title, page.LocalizedDisplayName, scope, descriptor.SettingId, T(descriptor.DescriptionKey), string.Join(' ', descriptor.Keywords))));
        }
        _searchIndex = new(entries);
        OnSearchQueryChanged(SearchQuery);
    }

    private static IEnumerable<SettingDescriptor> LocalDescriptors()
    {
        (string Id, string Page, string Title, SettingsScope Scope, string Keywords)[] items =
        [
            ("host.serverHttps", "system", "settings.server_https.title", SettingsScope.HostMachine, "HTTPS TLS certificate 证书 服务器 换证 証明書 サーバー"),
            ("workspace.colors", "personalization/colors", "settings.colors_and_mode", SettingsScope.Workspace, "colors mode theme light dark palette 颜色 模式 主题 外观 配色 色 テーマ ライト ダーク"),
            ("workspace.systemStyle", "personalization/style", "settings.system_style", SettingsScope.Workspace, "system style window menu overview chrome corners 系统风格 窗口 菜单 任务概览 圆角 システム スタイル ウィンドウ メニュー 角"),
            ("workspace.desktopLayout", "personalization/layout", "settings.desktop_layout", SettingsScope.Workspace, "desktop layout shell taskbar dock launcher 桌面布局 桌面样式 任务栏 启动器 デスクトップ レイアウト タスクバー"),
            ("workspace.wallpaper", "personalization/background", "settings.wallpaper", SettingsScope.Workspace, "wallpaper background 壁纸 背景 壁紙"),
            ("workspace.palette", "personalization/colors", "settings.palette", SettingsScope.Workspace, "palette color 颜色 配色 色"),
            ("workspace.accent", "personalization/colors", "settings.accent", SettingsScope.Workspace, "accent colour 强调色 アクセント"),
            ("workspace.customTheme", "personalization/colors", "settings.custom_theme", SettingsScope.Workspace, "import export theme 导入 导出 インポート"),
            ("workspace.timeFormat", "time-language", "settings.time.format", SettingsScope.Workspace, "clock 12h 24h 时钟 時計"),
            ("workspace.dateFormat", "time-language", "settings.date.format", SettingsScope.Workspace, "date 日期 日付"),
            ("workspace.language", "time-language", "settings.display_language", SettingsScope.Workspace, "language 中文 English 日本語 语言 言語"),
            ("workspace.region", "time-language", "settings.region_format", SettingsScope.Workspace, "region 区域 地域"),
            ("workspace.defaultApps", "default-apps", "settings.default_apps", SettingsScope.Workspace, "extension association 文件关联 拡張子"),
            ("client.interfaceScale", "accessibility", "settings.device.scale", SettingsScope.ClientDevice, "scale zoom text size 缩放 字号 拡大"),
            ("client.reducedMotion", "accessibility", "settings.device.motion", SettingsScope.ClientDevice, "animation motion 动画 アニメーション"),
            ("client.highContrast", "accessibility", "settings.device.contrast", SettingsScope.ClientDevice, "contrast visibility 对比度 コントラスト"),
            ("client.notifications", "system/preferences", "settings.device.notifications", SettingsScope.ClientDevice, "notification banner 通知 バナー"),
            ("client.doNotDisturb", "system/preferences", "settings.device.dnd", SettingsScope.ClientDevice, "quiet do not disturb 免打扰 おやすみ"),
            ("client.restoreTerminals", "system/preferences", "settings.device.restore", SettingsScope.ClientDevice, "startup restore terminal 启动 恢复 终端 起動 ターミナル"),
            ("client.connectionBar", "system/preferences", "settings.device.connection_bar", SettingsScope.ClientDevice, "connection bar fullscreen 连接 全屏 接続 フルスクリーン"),
            ("client.developer", "developer", "settings.developer_mode", SettingsScope.ClientDevice, "debug developer bridge 调试 开发 デバッグ"),
            ("client.diagnostics", "developer", "settings.network_inspector", SettingsScope.ClientDevice, "diagnostics request network 网络 诊断 診断"),
            ("client.apps", "apps", "settings.app_information", SettingsScope.ClientDevice, "install uninstall apps 安装 卸载 アプリ"),
            ("client.permissions", "apps", "settings.app_permissions", SettingsScope.ClientDevice, "permissions 授权 权限 権限"),
            ("account.alias", "account-security", "settings.account.title", SettingsScope.HostUser, "account security alias login 账号 安全 登录别名 アカウント セキュリティ ログイン エイリアス"),
            ("host.environment", "system", "settings.environment.title", SettingsScope.HostUser, "PATH environment 环境变量 路径 環境変数 パス"),
            ("workspace.environment", "system", "settings.workspace_environment.title", SettingsScope.Workspace, "workspace PATH environment 工作区 环境变量 ワークスペース 環境変数"),
            ("host.time.zone", "time-language", "settings.time_zone", SettingsScope.HostMachine, "timezone time zone 时区 タイムゾーン"),
            ("host.identity.hostname", "system", "settings.hostname", SettingsScope.HostMachine, "hostname computer name 主机名 计算机名 ホスト名 コンピューター名"),
            ("relaxkonos.about", "about", "settings.about_page.title", SettingsScope.ClientDevice, "about website source repository license legal 开源 官网 许可证 法律情報")
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
        NotifySaveFeedback(); RebuildSearchIndex(); OnPropertyChanged(nameof(Breadcrumb)); OnPropertyChanged(nameof(ParentTitle)); OnPropertyChanged(nameof(ConnectionSummary)); OnPropertyChanged(nameof(PageScopeDescription)); OnPropertyChanged(nameof(HasPageScopeDescription)); SearchLocationStatus = "";
    }
    private void OnNavigationSessionChanged(object? sender, AuthSessionStateChangedEventArgs args) => Dispatcher.UIThread.Post(() =>
    {
        if (_navigationDisposed) return;
        _catalogLifetime.Cancel(); _catalogLifetime.Dispose(); _catalogLifetime = new();
        _navigationHistory.Clear(); BackCommand.NotifyCanExecuteChanged();
        SearchLocationStatus = "";
        NavigationContextReset?.Invoke();
        _remoteDescriptors = Array.Empty<SettingDescriptor>(); CatalogProblem = "";
        RebuildSearchIndex(); OnPropertyChanged(nameof(ConnectionSummary)); OnPropertyChanged(nameof(ConnectionAddress));
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
