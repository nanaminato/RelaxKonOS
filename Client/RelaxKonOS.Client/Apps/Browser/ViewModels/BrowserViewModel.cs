using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Apps.Browser;
using RelaxKonOS.Protocol.Browser;

namespace RelaxKonOS.Client.Apps.Browser.ViewModels;

/// <summary>RemoteBrowser 主视图模型。
///
/// 数据流：
/// - 用户输入地址 → <see cref="NavigateCommand"/> → 更新当前标签，通过 <see cref="ViewNavigateRequested"/> 请求导航
/// - NativeWebView.NavigationStarted/Completed 事件由 View code-behind 转发到
///   <see cref="OnNavigationStarted"/> / <see cref="OnNavigationCompleted"/>，更新地址栏 + 记录历史
/// - 书签/历史通过 <see cref="IBrowserClient"/> 调用 Server REST API（JWT via IAuthSession）
/// - Sidebar 双标签页（书签 / 历史），点击条目导航，X 删除单条，"清空"清全部
///
/// 注意：网页在客户端渲染（Windows/macOS 使用 NativeWebView，Linux 委托宿主浏览器），
/// 网页内容走客户端网络而非 Server；Server 仅持久化书签与历史（按用户隔离）。</summary>
public sealed partial class BrowserViewModel : LocalizedObservableObject
{
    private readonly IBrowserClient _client;
    private BookmarkDto? _currentBookmark;
    private int _bookmarkOffset;
    private int _historyOffset;
    private int _bookmarkGeneration;
    private int _historyGeneration;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanLoadMoreBookmarks))] private bool _hasMoreBookmarks;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanLoadMoreHistory))] private bool _hasMoreHistory;
    [ObservableProperty] private bool _isSyncingCollections;
    private Uri? _currentUri;          // 当前 WebView 实际加载的 URI（区别于地址栏文本，可能正在输入未提交）

    public BrowserViewModel(IBrowserClient client)
    {
        _client = client;
        Bookmarks = new ObservableCollection<BookmarkDto>();
        History = new ObservableCollection<HistoryEntryDto>();
        AddTab();
    }

    public ObservableCollection<BrowserTabViewModel> Tabs { get; } = new();
    [ObservableProperty] private BrowserTabViewModel? _selectedTab;
    public Action<Uri>? ViewNavigateRequested { get; set; }
    public Action<BrowserTabViewModel>? ViewTabSelectedRequested { get; set; }
    public Action<BrowserTabViewModel>? ViewTabClosedRequested { get; set; }
    public bool HasCurrentPage => SelectedTab?.Source is not null;
    public string ConnectionSymbol => _currentUri?.Scheme == Uri.UriSchemeHttps ? "◈" : "ⓘ";
    public string ConnectionDescription => LocalizedText.Get(_currentUri?.Scheme == Uri.UriSchemeHttps
        ? "browser.connection_https" : "browser.connection_information");

    [RelayCommand]
    private void AddTab()
    {
        var tab = new BrowserTabViewModel();
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    [RelayCommand]
    private void CloseTab(BrowserTabViewModel? tab)
    {
        if (tab is null || !Tabs.Contains(tab)) return;
        var index = Tabs.IndexOf(tab);
        if (SelectedTab == tab)
            SelectedTab = Tabs.Count > 1 ? Tabs[index == 0 ? 1 : index - 1] : null;
        Tabs.Remove(tab);
        ViewTabClosedRequested?.Invoke(tab);
        if (Tabs.Count == 0) AddTab();
    }

    public void CloseSelectedTab() => CloseTab(SelectedTab);

    partial void OnSelectedTabChanged(BrowserTabViewModel? value)
    {
        if (value is not null) ViewTabSelectedRequested?.Invoke(value);
        _currentUri = value?.Source;
        AddressText = value?.Source?.ToString() ?? "";
        IsLoading = value?.IsLoading ?? false;
        StatusText = value?.Source is { } current
            ? LocalizedText.Ref(value.IsLoading ? "browser.status.loading_url" : "browser.status.completed", current)
            : LocalizedText.Ref("browser.status.ready");
        UpdateNavigationState(value?.CanGoBack ?? false, value?.CanGoForward ?? false);
        IsCurrentBookmarked = false;
        _currentBookmark = null;
        if (_currentUri is { } source) _ = RefreshBookmarkStarAsync(source);
        NotifyPageState();
    }

    private void NotifyPageState()
    {
        OnPropertyChanged(nameof(HasCurrentPage));
        OnPropertyChanged(nameof(ConnectionSymbol));
        OnPropertyChanged(nameof(ConnectionDescription));
    }

    /// <summary>书签列表（侧边栏"书签"标签页绑定）。</summary>
    public ObservableCollection<BookmarkDto> Bookmarks { get; }

    /// <summary>历史记录列表（侧边栏"历史记录"标签页绑定，按 LastVisitedAt 倒序）。</summary>
    public ObservableCollection<HistoryEntryDto> History { get; }

    [ObservableProperty] private string _addressText = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private LocalizedStatus _statusText;
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _canGoForward;
    [ObservableProperty] private bool _isCurrentBookmarked;
    public bool CanLoadMoreBookmarks => HasMoreBookmarks && ActiveSidebarTab == SidebarTab.Bookmarks;
    public bool CanLoadMoreHistory => HasMoreHistory && ActiveSidebarTab == SidebarTab.History;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanLoadMoreBookmarks)), NotifyPropertyChangedFor(nameof(CanLoadMoreHistory))] private SidebarTab _activeSidebarTab = SidebarTab.Bookmarks;
    [ObservableProperty] private bool _isSidebarVisible;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullScreenMenuText))]
    private bool _isFullScreen;
    [ObservableProperty] private string _homePageText = BrowserSettingsDto.Default.HomePage!;
    [ObservableProperty] private BrowserLinkOpenTarget _linkOpenTarget = BrowserLinkOpenTarget.BuiltInBrowser;

    /// <summary>主页 URI（地址栏"主页"按钮的目标）。可空：未设置时不显示主页按钮或 no-op。</summary>
    public Uri? HomePage { get; private set; } = new(BrowserSettingsDto.Default.HomePage!);

    /// <summary>关闭窗口回调（由 BrowserApp 注入）。</summary>
    public Action? CloseAction { get; set; }
    /// <summary>Host-provided action that switches the owning desktop window in or out of full screen.</summary>
    public Action? ToggleFullScreenAction { get; set; }
    public Func<Task>? RequestSettingsAsync { get; set; }
    public Action? CloseSettingsAction { get; set; }

    /// <summary>由 View code-behind 在 NativeWebView.CanGoBack/CanGoForward 变化时调用。</summary>
    public void UpdateNavigationState(bool canGoBack, bool canGoForward)
    {
        CanGoBack = canGoBack;
        CanGoForward = canGoForward;
        if (SelectedTab is { } tab)
        {
            tab.CanGoBack = canGoBack;
            tab.CanGoForward = canGoForward;
        }
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    /// <summary>由 View code-behind 在 NativeWebView.NavigationStarted 触发时调用。
    /// 更新地址栏为实际正在加载的 URI；记录"开始加载"状态。</summary>
    public void OnNavigationStarted(Uri url)
    {
        if (_currentUri != url) { _currentBookmark = null; IsCurrentBookmarked = false; }
        _currentUri = url;
        AddressText = url.IsAbsoluteUri ? url.ToString() : url.OriginalString;
        IsLoading = true;
        if (SelectedTab is { } tab) { tab.Source = url; tab.IsLoading = true; }
        NotifyPageState();
        StatusText = LocalizedText.Ref("browser.status.loading_url", url);
    }

    /// <summary>由 View code-behind 在 NativeWebView.NavigationCompleted 触发时调用。
    /// 成功加载时记录历史（去重，单次导航只记一次），刷新书签星标。</summary>
    public async void OnNavigationCompleted(Uri? url, bool isSuccess)
    {
        IsLoading = false;
        if (SelectedTab is { } tab) { tab.IsLoading = false; if (url is not null) tab.Source = url; }
        NotifyPageState();
        if (url is not null && isSuccess)
        {
            _currentUri = url;
            AddressText = url.ToString();
            StatusText = LocalizedText.Ref("browser.status.completed", url);
            // 异步记录到服务端历史（fire-and-forget 友好：失败只更新状态栏不阻塞 UI）
            _ = RecordVisitAsync(url);
            _ = RefreshBookmarkStarAsync(url);
        }
        else
        {
        StatusText = url is null
            ? LocalizedText.Ref("browser.status.stopped")
            : LocalizedText.Ref("browser.status.load_failed", url);
        }
    }

    /// <summary>由 View code-behind 在 NativeWebView.GoBack/GoForward 实际执行后调用，
    /// 同步当前 URI（用于历史记录与书签星标刷新）。</summary>
    public void OnNavigatedToExisting(Uri url)
    {
        _currentUri = url;
        AddressText = url.IsAbsoluteUri ? url.ToString() : url.OriginalString;
        _ = RefreshBookmarkStarAsync(url);
    }

    // ---- 导航命令 ----

    [RelayCommand]
    private Task NavigateAsync(string? address)
    {
        BrowserDiagnostics.Record("Browser navigation requested from command.");
        var uri = NormalizeAddress(address);
        if (uri is null)
        {
            StatusText = LocalizedText.Ref("browser.status.invalid_address");
            return Task.CompletedTask;
        }

        if (LinkOpenTarget == BrowserLinkOpenTarget.HostBrowser)
        {
            OpenWithHostRequested?.Invoke(uri);
            _currentUri = uri;
            AddressText = uri.ToString();
            StatusText = LocalizedText.Ref("browser.status.opened_on_host", uri);
            if (SelectedTab is { } hostTab) hostTab.Source = uri;
            NotifyPageState();
            _ = RecordVisitAsync(uri);
            return Task.CompletedTask;
        }

        if (_currentUri != uri) { _currentBookmark = null; IsCurrentBookmarked = false; }
        _currentUri = uri;
        AddressText = uri.ToString();
        if (SelectedTab is { } tab) { tab.Source = uri; tab.IsLoading = true; }
        IsLoading = true;
        StatusText = LocalizedText.Ref("browser.status.loading_url", uri);
        NotifyPageState();
        ViewNavigateRequested?.Invoke(uri);
        return Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => ViewGoBackRequested?.Invoke();

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward() => ViewGoForwardRequested?.Invoke();

    [RelayCommand]
    private void Refresh() => ViewRefreshRequested?.Invoke();

    [RelayCommand]
    private void Stop() => ViewStopRequested?.Invoke();

    [RelayCommand]
    private async Task GoHomeAsync()
    {
        if (HomePage is not null)
        {
            BrowserDiagnostics.Record($"Home command invoked: {BrowserDiagnostics.SanitizeUri(HomePage)}.");
            await NavigateAsync(HomePage.ToString());
        }
    }

    /// <summary>由 View code-behind 注入：当 GoBack/GoForward/Refresh/Stop 命令触发时，
    /// View 接管实际调用 NativeWebView 对应方法（VM 不持有 WebView 引用）。</summary>
    public Action? ViewGoBackRequested { get; set; }
    public Action? ViewGoForwardRequested { get; set; }
    public Action? ViewRefreshRequested { get; set; }
    public Action? ViewStopRequested { get; set; }
    /// <summary>由 View 注入，使用运行 RelaxKonOS Client 的宿主机默认浏览器打开链接。</summary>
    public Action<Uri>? OpenWithHostRequested { get; set; }

    public string FullScreenMenuText => IsFullScreen ? LocalizedText.Get("browser.exit_full_screen") : LocalizedText.Get("browser.enter_full_screen");

    [RelayCommand]
    private void ToggleFullScreen() => ToggleFullScreenAction?.Invoke();

    // ---- 书签 ----

    [RelayCommand]
    private async Task ToggleBookmarkAsync()
    {
        if (_currentUri is null) return;
        var url = _currentUri.IsAbsoluteUri ? _currentUri.ToString() : _currentUri.OriginalString;
        if (IsCurrentBookmarked)
        {
            // 找到当前 URL 对应书签并删除
            var bm = _currentBookmark;
            if (bm is not null)
            {
                try
                {
                    await _client.DeleteBookmarkAsync(bm.Id);
                    Bookmarks.Remove(bm);
                    IsCurrentBookmarked = false;
                    _currentBookmark = null;
                    StatusText = LocalizedText.Ref("browser.status.bookmark_deleted", url);
                }
                catch (Exception ex) { StatusText = LocalizedText.Ref("browser.status.bookmark_delete_failed", ex.Message); }
            }
        }
        else
        {
            var title = url;
            try
            {
                var dto = await _client.AddBookmarkAsync(title, url);
                await ReloadBookmarksAsync();
                _currentBookmark = dto;
                IsCurrentBookmarked = true;
                StatusText = LocalizedText.Ref("browser.status.bookmark_added", title);
            }
            catch (Exception ex) { StatusText = LocalizedText.Ref("browser.status.bookmark_add_failed", ex.Message); }
        }
    }

    [RelayCommand]
    private async Task OpenBookmarkAsync(BookmarkDto? bookmark)
    {
        if (bookmark is null) return;
        await NavigateAsync(bookmark.Url);
    }

    [RelayCommand]
    private async Task DeleteBookmarkAsync(BookmarkDto? bookmark)
    {
        if (bookmark is null) return;
        try
        {
            await _client.DeleteBookmarkAsync(bookmark.Id);
            await ReloadBookmarksAsync();
            if (_currentUri is not null && bookmark.Url == (_currentUri.IsAbsoluteUri ? _currentUri.ToString() : _currentUri.OriginalString))
            {
                IsCurrentBookmarked = false;
                _currentBookmark = null;
            }
            StatusText = LocalizedText.Ref("browser.status.bookmark_deleted", bookmark.Title);
        }
        catch (Exception ex) { StatusText = LocalizedText.Ref("browser.status.bookmark_delete_failed", ex.Message); }
    }

    [RelayCommand]
    private async Task ClearBookmarksAsync()
    {
        try
        {
            await _client.ClearBookmarksAsync();
            ++_bookmarkGeneration;
            Bookmarks.Clear();
            _bookmarkOffset = 0;
            HasMoreBookmarks = false;
            _currentBookmark = null;
            IsCurrentBookmarked = false;
            StatusText = LocalizedText.Ref("browser.status.bookmarks_cleared");
        }
        catch (Exception ex) { StatusText = LocalizedText.Ref("browser.status.bookmarks_clear_failed", ex.Message); }
    }

    // ---- 历史 ----

    [RelayCommand]
    private async Task OpenHistoryAsync(HistoryEntryDto? entry)
    {
        if (entry is null) return;
        await NavigateAsync(entry.Url);
    }

    [RelayCommand]
    private async Task DeleteHistoryAsync(HistoryEntryDto? entry)
    {
        if (entry is null) return;
        try
        {
            await _client.DeleteHistoryAsync(entry.Id);
            await ReloadHistoryAsync();
            StatusText = LocalizedText.Ref("browser.status.history_deleted", entry.Title);
        }
        catch (Exception ex) { StatusText = LocalizedText.Ref("browser.status.history_delete_failed", ex.Message); }
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        try
        {
            await _client.ClearHistoryAsync();
            ++_historyGeneration;
            History.Clear();
            _historyOffset = 0;
            HasMoreHistory = false;
            StatusText = LocalizedText.Ref("browser.status.history_cleared");
        }
        catch (Exception ex) { StatusText = LocalizedText.Ref("browser.status.history_clear_failed", ex.Message); }
    }

    // ---- 侧边栏切换 ----

    [RelayCommand]
    private void SwitchToBookmarks()
    {
        ActiveSidebarTab = SidebarTab.Bookmarks;
        IsSidebarVisible = true;
    }

    [RelayCommand]
    private void SwitchToHistory()
    {
        ActiveSidebarTab = SidebarTab.History;
        IsSidebarVisible = true;
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    [RelayCommand]
    private async Task OpenSettingsAsync()
        => await (RequestSettingsAsync?.Invoke() ?? Task.CompletedTask);

    [RelayCommand]
    private void CloseSettings() => CloseSettingsAction?.Invoke();

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();

    [RelayCommand]
    private async Task SaveBrowserSettingsAsync()
    {
        if (!Uri.TryCreate(HomePageText, UriKind.Absolute, out var homePage)
            || (homePage.Scheme != Uri.UriSchemeHttp && homePage.Scheme != Uri.UriSchemeHttps))
        {
            StatusText = LocalizedText.Ref("browser.settings.invalid_home_page");
            return;
        }
        try
        {
            // The link-opening preference is owned by Settings > Applications > Browser.
            // Read the current value before saving the home page so this dialog can never
            // overwrite a preference changed from Settings while the browser is open.
            var current = await _client.GetSettingsAsync();
            var saved = await _client.SaveSettingsAsync(new BrowserSettingsDto(
                homePage.AbsoluteUri, current.LinkOpenTarget));
            HomePageText = saved.HomePage ?? BrowserSettingsDto.Default.HomePage!;
            HomePage = new Uri(HomePageText);
            LinkOpenTarget = saved.LinkOpenTarget;
            StatusText = LocalizedText.Ref("browser.settings.saved");
        }
        catch (Exception ex)
        {
            StatusText = LocalizedText.Ref("browser.settings.save_failed", ex.Message);
        }
    }

    // ---- 初始化加载 ----

    /// <summary>登录后由 BrowserApp 调用：加载书签列表 + 最近历史。</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        StatusText = LocalizedText.Ref("browser.status.syncing");
        try
        {
            await ReloadBookmarksAsync();
            await ReloadHistoryAsync();
            var settings = await _client.GetSettingsAsync();
            HomePageText = settings.HomePage ?? BrowserSettingsDto.Default.HomePage!;
            HomePage = new Uri(HomePageText);
            LinkOpenTarget = settings.LinkOpenTarget;
            StatusText = LocalizedText.Ref("browser.status.summary", Bookmarks.Count, History.Count);
        }
        catch (Exception ex)
        {
            StatusText = LocalizedText.Ref("browser.status.sync_failed", ex.Message);
        }
        finally { IsLoading = false; }
    }

    /// <summary>记录一次访问到服务端历史（仅 fire-and-forget 调用，错误不抛出）。</summary>
    internal void RecordBackgroundVisit(Uri url) => _ = RecordVisitAsync(url);

    private async Task RecordVisitAsync(Uri url)
    {
        try
        {
            var urlStr = url.IsAbsoluteUri ? url.ToString() : url.OriginalString;
            await _client.RecordVisitAsync(urlStr, urlStr);
            // A visit changes ordering; restart the bounded first page rather than using a stale offset.
            await ReloadHistoryAsync();
        }
        catch
        {
            // 历史记录失败不阻塞浏览
        }
    }

    /// <summary>刷新当前 URL 是否已加书签（用于星标 UI）。</summary>
    private async Task RefreshBookmarkStarAsync(Uri url)
    {
        try
        {
            var result = await _client.ListBookmarksAsync(limit: 1, url: url.ToString());
            if (_currentUri != url) return;
            _currentBookmark = result.FirstOrDefault();
            IsCurrentBookmarked = _currentBookmark is not null;
        }
        catch (Exception ex) { StatusText = LocalizedText.Ref("browser.status.sync_failed", ex.Message); }
    }

    private async Task ReloadBookmarksAsync()
    {
        var generation = ++_bookmarkGeneration;
        var page = await _client.ListBookmarksAsync();
        if (generation != _bookmarkGeneration) return;
        Bookmarks.Clear();
        foreach (var item in page) Bookmarks.Add(item);
        _bookmarkOffset = page.Count;
        HasMoreBookmarks = page.Count == BrowserQueryLimits.DefaultPageSize;
    }

    private async Task ReloadHistoryAsync()
    {
        var generation = ++_historyGeneration;
        var page = await _client.ListHistoryAsync();
        if (generation != _historyGeneration) return;
        History.Clear();
        foreach (var item in page) History.Add(item);
        _historyOffset = page.Count;
        HasMoreHistory = page.Count == BrowserQueryLimits.DefaultPageSize;
    }

    [RelayCommand]
    private async Task LoadMoreBookmarksAsync()
    {
        if (!HasMoreBookmarks || IsSyncingCollections) return;
        IsSyncingCollections = true;
        try
        {
            var generation = _bookmarkGeneration;
            var page = await _client.ListBookmarksAsync(offset: _bookmarkOffset);
            if (generation != _bookmarkGeneration) return;
            foreach (var item in page)
                if (!Bookmarks.Any(existing => existing.Id == item.Id)) Bookmarks.Add(item);
            _bookmarkOffset += page.Count;
            HasMoreBookmarks = page.Count == BrowserQueryLimits.DefaultPageSize;
        }
        catch (Exception ex) { StatusText = LocalizedText.Ref("browser.status.sync_failed", ex.Message); }
        finally { IsSyncingCollections = false; }
    }

    [RelayCommand]
    private async Task LoadMoreHistoryAsync()
    {
        if (!HasMoreHistory || IsSyncingCollections) return;
        IsSyncingCollections = true;
        try
        {
            var generation = _historyGeneration;
            var page = await _client.ListHistoryAsync(offset: _historyOffset);
            if (generation != _historyGeneration) return;
            foreach (var item in page)
                if (!History.Any(existing => existing.Id == item.Id)) History.Add(item);
            _historyOffset += page.Count;
            HasMoreHistory = page.Count == BrowserQueryLimits.DefaultPageSize;
        }
        catch (Exception ex) { StatusText = LocalizedText.Ref("browser.status.sync_failed", ex.Message); }
        finally { IsSyncingCollections = false; }
    }

    /// <summary>把用户输入归一为绝对 Uri。已是绝对 URL 直接用；否则尝试加 https:// 前缀；
    /// 形如 "example.com foo"（含空格）当作搜索引擎查询（用 bing）。null/空返回 null。</summary>
    private static Uri? NormalizeAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var trimmed = address.Trim();
        if (Uri.IsWellFormedUriString(trimmed, UriKind.Absolute))
            return new Uri(trimmed);
        // localhost:9999 is a normal browser address even without an explicit scheme.
        if (Uri.TryCreate("http://" + trimmed, UriKind.Absolute, out var loopback)
            && IsLoopbackAddress(loopback))
            return loopback;
        // 看起来像域名（无 scheme）—— 补 https://
        if (trimmed.Contains('.') && !trimmed.Contains(' '))
            return new Uri("https://" + trimmed);
        // 否则当作搜索查询
        return new Uri("https://www.bing.com/search?q=" + Uri.EscapeDataString(trimmed));
    }

    private static bool IsLoopbackAddress(Uri uri)
        => uri.IsAbsoluteUri
           && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host == "127.0.0.1")
           && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
}

/// <summary>侧边栏标签页枚举。</summary>
public enum SidebarTab
{
    Bookmarks,
    History,
}
