using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using System.ComponentModel;
using System.Diagnostics;
using RelaxKonOS.Client.Apps.Browser;
using RelaxKonOS.Client.Apps.Browser.ViewModels;
using RelaxKonOS.Protocol.Browser;

namespace RelaxKonOS.Client.Apps.Browser.Views;

/// <summary>Bridges browser navigation to the view-model and selects the supported native host per platform.</summary>
public partial class BrowserMainView : UserControl
{
    private const double DefaultSidebarWidth = 320;
    private const double SidebarSplitterWidth = 4;

    private BrowserViewModel? _observedViewModel;
    private readonly bool _useExternalBrowser = OperatingSystem.IsLinux();
    private NativeWebView? _webView;
    private readonly Grid _surfaces = new();
    private readonly Dictionary<BrowserTabViewModel, NativeWebView> _tabViews = new();
    private bool _nativeVisible = true;
    private bool _closed;
    private int _openMenus;
    private double _sidebarWidth = DefaultSidebarWidth;

    private ColumnDefinition SidebarColumn => BrowserContentGrid.ColumnDefinitions[2];
    private ColumnDefinition SidebarSplitterColumn => BrowserContentGrid.ColumnDefinitions[1];

    public BrowserMainView()
    {
        InitializeComponent();
        BrowserDiagnostics.Record(_useExternalBrowser
            ? "BrowserMainView initialized; Linux will use the system browser process."
            : "BrowserMainView initialized; creating embedded NativeWebView.");
        // Native surfaces are created lazily so an empty tab has no heavyweight adapter.
        if (!_useExternalBrowser) WebViewHost.Content = _surfaces;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) =>
        {
            TabStrip.MaxWidth = Math.Max(120, Bounds.Width - 56);
            if (ViewModel is { } model) UpdateSidebarLayout(model.IsSidebarVisible);
        };
    }

    /// <summary>Moves keyboard focus to the address field and selects the current address.</summary>
    public void FocusAddressBox()
    {
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void CreateEmbeddedWebView(BrowserTabViewModel tab)
    {
        var webView = new NativeWebView();
        webView.EnvironmentRequested += ConfigureWebViewEnvironment;
        webView.NavigationStarted += (_, args) =>
        {
            if (_closed || !_tabViews.ContainsKey(tab)) return;
            if (ViewModel?.SelectedTab == tab && TryOpenNativeLinkOnHost(args)) return;
            tab.Source = webView.Source;
            tab.IsLoading = true;
            tab.CanGoBack = webView.CanGoBack;
            tab.CanGoForward = webView.CanGoForward;
            if (ViewModel?.SelectedTab == tab)
                OnNavigationStarted(webView.Source, webView.CanGoBack, webView.CanGoForward, "NativeWebView");
        };
        webView.NavigationCompleted += (_, _) =>
        {
            if (_closed || !_tabViews.ContainsKey(tab)) return;
            tab.Source = webView.Source;
            tab.IsLoading = false;
            tab.CanGoBack = webView.CanGoBack;
            tab.CanGoForward = webView.CanGoForward;
            if (ViewModel?.SelectedTab == tab)
                OnNavigationCompleted(webView.Source, webView.CanGoBack, webView.CanGoForward, "NativeWebView");
            else if (webView.Source is { } source)
                ViewModel?.RecordBackgroundVisit(source);
        };
        _tabViews.Add(tab, webView);
        _surfaces.Children.Add(webView);
        _webView = webView;
        UpdateNativeVisibility();
    }

    private void SelectTab(BrowserTabViewModel tab)
    {
        _webView = _tabViews.GetValueOrDefault(tab);
        UpdateNativeVisibility();
    }

    private void CloseTabSurface(BrowserTabViewModel tab)
    {
        if (!_tabViews.Remove(tab, out var surface)) return;
        surface.Stop();
        surface.IsVisible = false;
        _surfaces.Children.Remove(surface);
    }

    private void UpdateNativeVisibility()
    {
        foreach (var surface in _tabViews.Values)
            surface.IsVisible = _nativeVisible && _openMenus == 0 && ReferenceEquals(surface, _webView);
    }

    private static void ConfigureWebViewEnvironment(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        BrowserDiagnostics.Record($"NativeWebView environment requested: {e.GetType().FullName}.");
        if (!OperatingSystem.IsLinux())
            return;

        // Avalonia 12 exposes this switch on its Linux-specific event args, while the
        // public event uses the platform-neutral base type. Use the runtime type here so
        // non-Linux builds do not need a reference to the internal backend type.
        try
        {
            ((dynamic)e).PreferWebKitGtkInstead = true;
            BrowserDiagnostics.Record("Requested WebKitGTK backend on Linux.");
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            BrowserDiagnostics.Record("Linux WebView backend selection is unavailable in this runtime.");
        }
    }

    private BrowserViewModel? ViewModel => DataContext as BrowserViewModel;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        BrowserDiagnostics.Record("BrowserMainView loaded.");
        WireWebViewCommands();
        ObserveViewModel();
        // Let an embedded WebView receive keyboard input.
        _webView?.Focus();
    }

    /// <summary>把 VM 的 GoBack/GoForward/Refresh/Stop 命令接到 NativeWebView 的实际方法。</summary>
    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        BrowserDiagnostics.Record("BrowserMainView unloaded.");
        if (_observedViewModel is null)
            return;

        _observedViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _observedViewModel = null;
    }

    /// <summary>Keeps an embedded platform-native WebView from floating above inactive windows.</summary>
    public void SetWebViewVisible(bool isVisible)
    {
        _nativeVisible = isVisible;
        UpdateNativeVisibility();
    }

    public void ClosePlatformBrowser()
    {
        if (_closed) return;
        _closed = true;
        foreach (var tab in _tabViews.Keys.ToArray()) CloseTabSurface(tab);
        if (_observedViewModel is { } model)
        {
            model.PropertyChanged -= ViewModel_PropertyChanged;
            model.ViewNavigateRequested = null;
            model.ViewTabSelectedRequested = null;
            model.ViewTabClosedRequested = null;
            model.ViewGoBackRequested = null;
            model.ViewGoForwardRequested = null;
            model.ViewRefreshRequested = null;
            model.ViewStopRequested = null;
            model.OpenWithHostRequested = null;
        }
        _observedViewModel = null;
        _webView = null;
    }

    private void ObserveViewModel()
    {
        var viewModel = ViewModel;
        if (ReferenceEquals(_observedViewModel, viewModel))
            return;

        if (_observedViewModel is not null)
            _observedViewModel.PropertyChanged -= ViewModel_PropertyChanged;

        _observedViewModel = viewModel;
        if (viewModel is null)
            return;

        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateSidebarLayout(viewModel.IsSidebarVisible);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserViewModel.IsSidebarVisible) && sender is BrowserViewModel viewModel)
            UpdateSidebarLayout(viewModel.IsSidebarVisible);
    }

    private void UpdateSidebarLayout(bool isVisible)
    {
        if (isVisible)
        {
            var available = BrowserContentGrid.Bounds.Width;
            SidebarColumn.Width = new GridLength(available > 0 ? Math.Min(_sidebarWidth, available * 0.45) : _sidebarWidth, GridUnitType.Pixel);
            SidebarSplitterColumn.Width = new GridLength(SidebarSplitterWidth, GridUnitType.Pixel);
            return;
        }

        if (SidebarColumn.ActualWidth > 0)
            _sidebarWidth = SidebarColumn.ActualWidth;

        SidebarColumn.Width = new GridLength(0, GridUnitType.Pixel);
        SidebarSplitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
    }

    private void WireWebViewCommands()
    {
        if (ViewModel is null) return;
        ViewModel.ViewNavigateRequested = NavigatePlatformWebView;
        ViewModel.ViewTabSelectedRequested = SelectTab;
        ViewModel.ViewTabClosedRequested = CloseTabSurface;
        if (ViewModel.SelectedTab is { } tab) SelectTab(tab);
        ViewModel.ViewGoBackRequested = () => _webView?.GoBack();
        ViewModel.ViewGoForwardRequested = () => _webView?.GoForward();
        ViewModel.ViewRefreshRequested = () =>
        {
            if (_webView is not null)
                _webView.Refresh();
            else if (ViewModel.SelectedTab?.Source is { } source)
                OpenWithSystemBrowser(source);
        };
        ViewModel.ViewStopRequested = () => _webView?.Stop();
        ViewModel.OpenWithHostRequested = source => OpenWithSystemBrowser(source, reportNavigation: false);
        ViewModel.UpdateNavigationState(_webView?.CanGoBack ?? false, _webView?.CanGoForward ?? false);
    }

    private void CloseTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BrowserTabViewModel tab })
            ViewModel?.CloseTabCommand.Execute(tab);
        e.Handled = true;
    }

    private void FocusAddress_Click(object? sender, RoutedEventArgs e) => FocusAddressBox();

    // ---- 地址栏 ----

    private void AddressBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb)
        {
            ViewModel?.NavigateCommand.Execute(tb.Text);
            e.Handled = true;
        }
    }

    private void MenuOpened(object? sender, EventArgs e)
    {
        ++_openMenus;
        UpdateNativeVisibility();
    }

    private void MenuClosed(object? sender, EventArgs e)
    {
        _openMenus = Math.Max(0, _openMenus - 1);
        UpdateNativeVisibility();
    }

    private void OpenEntry_Click(object? sender, RoutedEventArgs e)
    {
        var url = (sender as Control)?.DataContext switch
        {
            BookmarkDto bookmark => bookmark.Url,
            BrowserHistoryRow history => history.Item.Url,
            _ => null
        };
        if (url is not null) ViewModel?.NavigateCommand.Execute(url);
    }

    private async void EntryMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu || ViewModel is not { } model) return;
        var bookmark = menu.DataContext as BookmarkDto;
        var history = (menu.DataContext as BrowserHistoryRow)?.Item;
        var url = bookmark?.Url ?? history?.Url;
        if (url is null) return;
        try
        {
            switch (menu.Tag as string)
            {
                case "open": await model.NavigateCommand.ExecuteAsync(url); break;
                case "new-tab":
                    model.AddTabCommand.Execute(null);
                    await model.NavigateCommand.ExecuteAsync(url);
                    break;
                case "copy":
                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("Clipboard is unavailable.");
                    await clipboard.SetTextAsync(url);
                    model.StatusText = RelaxKonOS.Client.Localization.LocalizedText.Ref("browser.copied_link");
                    break;
                case "delete" when bookmark is not null: await model.DeleteBookmarkCommand.ExecuteAsync(bookmark); break;
                case "delete" when history is not null: await model.DeleteHistoryCommand.ExecuteAsync(history); break;
            }
        }
        catch (Exception error)
        {
            model.StatusText = RelaxKonOS.Client.Localization.LocalizedText.Ref("browser.entry_failed", error.Message);
        }
    }

    private void NavigatePlatformWebView(Uri source)
    {
        if (!_useExternalBrowser && _webView is null && ViewModel?.SelectedTab is { } tab)
            CreateEmbeddedWebView(tab);
        if (_webView is not null)
        {
            if (_webView.Source == source) { _webView.Refresh(); return; }
            _webView.Navigate(source);
            return;
        }

        OpenWithSystemBrowser(source);
    }

    private void OpenWithSystemBrowser(Uri source, bool reportNavigation = true)
    {
        try
        {
            var startInfo = new ProcessStartInfo(source.AbsoluteUri)
            {
                UseShellExecute = true,
            };
            Process.Start(startInfo);
            BrowserDiagnostics.Record($"Delegated navigation to the host browser: {BrowserDiagnostics.SanitizeUri(source)}.");
            if (reportNavigation)
            {
                OnNavigationStarted(source, false, false, "SystemBrowser");
                OnNavigationCompleted(source, false, false, "SystemBrowser");
            }
        }
        catch (Exception exception)
        {
            BrowserDiagnostics.Record($"Host browser launch failed: {exception.GetType().Name}: {exception.Message}");
            if (reportNavigation)
                ViewModel?.OnNavigationCompleted(source, isSuccess: false);
        }
    }

    /// <summary>Cancel an embedded, user-clicked HTTP(S) navigation when the user selected the host browser.</summary>
    private bool TryOpenNativeLinkOnHost(object? navigationArgs)
    {
        var source = _webView?.Source;
        if (ViewModel?.LinkOpenTarget != BrowserLinkOpenTarget.HostBrowser || source is null
            || (!source.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !source.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            return false;

        try
        {
            // Avalonia exposes a platform-neutral event but currently uses platform-specific
            // argument types. All supported adapters expose Cancel; dynamic keeps this source
            // independent of backend-only assemblies.
            ((dynamic)navigationArgs!).Cancel = true;
            OpenWithSystemBrowser(source);
            return true;
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // A backend without cancellable navigation still keeps the in-app page usable.
            return false;
        }
    }

    private void OnNavigationStarted(Uri? url, bool canGoBack, bool canGoForward, string host)
    {
        BrowserDiagnostics.Record($"{host} navigation started: {BrowserDiagnostics.SanitizeUri(url)}.");
        if (ViewModel is null)
            return;

        if (url is not null)
            ViewModel.OnNavigationStarted(url);
        ViewModel.UpdateNavigationState(canGoBack, canGoForward);
    }

    private void OnNavigationCompleted(Uri? url, bool canGoBack, bool canGoForward, string host)
    {
        BrowserDiagnostics.Record($"{host} navigation completed: {BrowserDiagnostics.SanitizeUri(url)}.");
        if (ViewModel is null)
            return;

        ViewModel.OnNavigationCompleted(url, isSuccess: true);
        ViewModel.UpdateNavigationState(canGoBack, canGoForward);
    }
}
