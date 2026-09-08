using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    private const double DefaultSidebarWidth = 260;
    private const double SidebarSplitterWidth = 4;

    private BrowserViewModel? _observedViewModel;
    private readonly bool _useExternalBrowser = OperatingSystem.IsLinux();
    private NativeWebView? _webView;
    private double _sidebarWidth = DefaultSidebarWidth;

    private ColumnDefinition SidebarColumn => BrowserContentGrid.ColumnDefinitions[0];
    private ColumnDefinition SidebarSplitterColumn => BrowserContentGrid.ColumnDefinitions[1];

    public BrowserMainView()
    {
        InitializeComponent();
        BrowserDiagnostics.Record(_useExternalBrowser
            ? "BrowserMainView initialized; Linux will use the system browser process."
            : "BrowserMainView initialized; creating embedded NativeWebView.");
        // On Linux, retain the localized XAML fallback; other platforms host NativeWebView.
        if (!_useExternalBrowser)
            CreateEmbeddedWebView();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Moves keyboard focus to the address field and selects the current address.</summary>
    public void FocusAddressBox()
    {
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void CreateEmbeddedWebView()
    {
        _webView = new NativeWebView();
        _webView.EnvironmentRequested += ConfigureWebViewEnvironment;
        _webView.AdapterCreated += (_, _) => BrowserDiagnostics.Record($"NativeWebView adapter created: {_webView.AdapterInfo?.ToString() ?? "<unknown>"}.");
        _webView.AdapterDestroyed += (_, _) => BrowserDiagnostics.Record("NativeWebView adapter destroyed.");
        _webView.NavigationStarted += (_, args) =>
        {
            if (TryOpenNativeLinkOnHost(args)) return;
            OnNavigationStarted(_webView.Source, _webView.CanGoBack, _webView.CanGoForward, "NativeWebView");
        };
        _webView.NavigationCompleted += (_, _) => OnNavigationCompleted(_webView.Source, _webView.CanGoBack, _webView.CanGoForward, "NativeWebView");
        WebViewHost.Content = _webView;
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
        if (_webView is not null)
        {
            _webView.IsVisible = isVisible;
            return;
        }

        // The Linux browser is an independent system process and must not be controlled by
        // the RelaxKonOS window manager. This avoids the WebKitGTK UI-thread deadlock.
    }

    public void ClosePlatformBrowser() { }

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
        if (e.PropertyName == nameof(BrowserViewModel.WebViewSource) && sender is BrowserViewModel navigationViewModel)
        {
            BrowserDiagnostics.Record($"Navigation assigned to platform WebView: {BrowserDiagnostics.SanitizeUri(navigationViewModel.WebViewSource)}.");
            if (navigationViewModel.WebViewSource is { } source)
                NavigatePlatformWebView(source);
        }

        if (e.PropertyName == nameof(BrowserViewModel.IsSidebarVisible) && sender is BrowserViewModel viewModel)
            UpdateSidebarLayout(viewModel.IsSidebarVisible);
    }

    private void UpdateSidebarLayout(bool isVisible)
    {
        if (isVisible)
        {
            SidebarColumn.Width = new GridLength(_sidebarWidth, GridUnitType.Pixel);
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
        ViewModel.ViewGoBackRequested = () => _webView?.GoBack();
        ViewModel.ViewGoForwardRequested = () => _webView?.GoForward();
        ViewModel.ViewRefreshRequested = () =>
        {
            if (_webView is not null)
                _webView.Refresh();
            else if (ViewModel.WebViewSource is { } source)
                OpenWithSystemBrowser(source);
        };
        ViewModel.ViewStopRequested = () => _webView?.Stop();
        ViewModel.OpenWithHostRequested = source => OpenWithSystemBrowser(source, reportNavigation: false);
        ViewModel.UpdateNavigationState(_webView?.CanGoBack ?? false, _webView?.CanGoForward ?? false);
    }

    // ---- 地址栏 ----

    private void AddressBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb)
        {
            ViewModel?.NavigateCommand.Execute(tb.Text);
            e.Handled = true;
        }
    }

    private void GoButton_Click(object? sender, RoutedEventArgs e)
        => ViewModel?.NavigateCommand.Execute(AddressBox.Text);

    // ---- 侧边栏列表 ----

    private void BookmarksList_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (BookmarksList.SelectedItem is BookmarkDto bm)
            ViewModel?.NavigateCommand.Execute(bm.Url);
    }

    private void HistoryList_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryEntryDto h)
            ViewModel?.NavigateCommand.Execute(h.Url);
    }

    /// <summary>侧边栏每行的"✕"删除按钮。Tag 区分 bookmark / history，DataContext 是该行 DTO。</summary>
    private void DeleteItemButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || ViewModel is null) return;
        switch (btn.Tag as string)
        {
            case "bookmark" when btn.DataContext is BookmarkDto bm:
                _ = ViewModel.DeleteBookmarkCommand.ExecuteAsync(bm);
                break;
            case "history" when btn.DataContext is HistoryEntryDto h:
                _ = ViewModel.DeleteHistoryCommand.ExecuteAsync(h);
                break;
        }
    }

    private void NavigatePlatformWebView(Uri source)
    {
        if (_webView is not null)
        {
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
