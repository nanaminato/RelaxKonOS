using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.WindowLayout;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.Diagnostics;
using RelaxKonOS.Client.Services.SystemUi;
using RelaxKonOS.Client.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace RelaxKonOS.Client.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _hideBarTimer;
    private bool _isPinned;
    private bool _isFullScreen;
    private bool _isDraggingConnectionBar;
    private Point _connectionDragStart;
    private double _connectionBarOffset;
    private WindowState _windowStateBeforeFullScreen = WindowState.Maximized;
    private readonly LocalizationService _localization;
    private SystemUiCoordinator? _systemUi;
    private int _desktopLoadGeneration;
    private bool _isDisconnecting;
    private readonly DoubleTransition _disconnectOverlayFade = new() { Property = OpacityProperty };

    public MainWindow()
    {
        InitializeComponent();
        _localization = App.Services.GetRequiredService<LocalizationService>();
        _localization.LanguageChanged += (_, _) => RefreshLocalizedText();
        RefreshLocalizedText();
        DisconnectingOverlay.Transitions = new Transitions { _disconnectOverlayFade };
        _hideBarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _hideBarTimer.Tick += (_, _) => HideConnectionBar();
        SizeChanged += (_, _) => ApplyConnectionBarOffset();
        DataContextChanged += async (_, _) => await AttachShellAsync();
        Opened += async (_, _) => await AttachShellAsync();
        // System shortcuts must run before a managed application's own key handler.  They own
        // desktop-wide navigation, whereas application shortcuts are only meaningful inside the
        // active window.  The existing XAML KeyDown hook remains the bubbling fallback for the
        // host-only keys below.
        Root.AddHandler(InputElement.KeyDownEvent, Root_OnKeyDown, RoutingStrategies.Tunnel);
        AttachSystemUi();
    }

    /// <summary>
    /// Connects the host's system UI layer. The overview view is fed by the coordinator and its
    /// visibility is mirrored here, so the overlay is only hit-testable while it is actually open.
    /// </summary>
    private void AttachSystemUi()
    {
        _systemUi = App.Services.GetRequiredService<SystemUiCoordinator>();
        WindowOverview.DataContext = _systemUi;
        _systemUi.Changed += (_, _) => WindowOverview.IsVisible = _systemUi.IsOverviewVisible;
    }

    private async Task AttachShellAsync()
    {
        if (DataContext is not DesktopShellViewModel shell) return;

        var generation = ++_desktopLoadGeneration;
        var started = DateTime.UtcNow;
        DesktopLoadingOverlay.IsVisible = true;
        shell.RequestToggleHostFullScreen = () => SetFullScreen(!_isFullScreen);
        shell.IsHostFullScreen = _isFullScreen;
        await App.Services.GetRequiredService<ShellRuntime>().AttachAsync(ShellHost, shell);

        // A tiny minimum keeps a cached shell from flashing a blank frame between Login and Desktop.
        var remaining = TimeSpan.FromMilliseconds(420) - (DateTime.UtcNow - started);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        if (generation == _desktopLoadGeneration)
        {
            DesktopLoadingOverlay.IsVisible = false;
            // First-time setup shows a modal dialog. It must run only after the loading
            // overlay is hidden, otherwise the dialog is unreachable behind it.
            await shell.TryTriggerFirstTimeSetupAsync();
        }
    }

    private void ConnectionInfo_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ConnectionInfo.IsVisible = !ConnectionInfo.IsVisible;

    private void Pin_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        PinButton.Content = _isPinned ? "●" : "○";
        ToolTip.SetTip(PinButton, T(_isPinned ? "shell.connection_bar.unpin_tooltip" : "shell.connection_bar.pin_tooltip", _isPinned ? "Unpin connection bar" : "Pin connection bar"));
        if (_isPinned)
        {
            _hideBarTimer.Stop();
            ConnectionBar.IsVisible = true;
        }
    }

    private void FullScreen_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SetFullScreen(!_isFullScreen);
    }

    private void SetFullScreen(bool fullScreen)
    {
        if (fullScreen == _isFullScreen)
            return;

        if (fullScreen)
            _windowStateBeforeFullScreen = WindowState;

        _isFullScreen = fullScreen;
        if (DataContext is DesktopShellViewModel shell)
            shell.IsHostFullScreen = _isFullScreen;
        WindowState = _isFullScreen ? WindowState.FullScreen : _windowStateBeforeFullScreen;
        FullScreenButton.Content = _isFullScreen ? "↙" : "↗";
        ToolTip.SetTip(FullScreenButton, T(_isFullScreen ? "shell.full_screen.exit" : "shell.full_screen.enter_tooltip", _isFullScreen ? "Exit full screen" : "Enter full screen"));
        ConnectionInfo.IsVisible = false;
        WindowTitleBar.IsVisible = !_isFullScreen;
        Root.Margin = new Thickness(0, _isFullScreen ? 0 : 34, 0, 0);

        if (_isFullScreen && !_isPinned)
            ScheduleConnectionBarHide();
        else
            ConnectionBar.IsVisible = true;
    }

    private async void Disconnect_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await DisconnectAsync();
    }

    private async void CloseWindow_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await DisconnectAsync();
    }

    private async Task DisconnectAsync()
    {
        if (_isDisconnecting)
            return;

        _isDisconnecting = true;
        ConnectionInfo.IsVisible = false;
        ShowDisconnectingOverlay();
        try
        {
            await App.Services.GetRequiredService<WindowLayoutStore>().FlushAsync();
            await App.Services.GetRequiredService<IAuthSession>().LogoutAsync();
        }
        finally
        {
            Close();
        }
    }

    /// <summary>
    /// Shows feedback before the final layout sync and sign-out request yield control.  Without this,
    /// a slow network makes the desktop look unresponsive until the window disappears.
    /// </summary>
    private void ShowDisconnectingOverlay()
    {
        _disconnectOverlayFade.Duration = this.TryFindResource("TransitionFast", out var value) && value is TimeSpan span
            ? span
            : TimeSpan.FromMilliseconds(120);
        DisconnectingOverlay.Opacity = 0;
        DisconnectingOverlay.IsVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (_isDisconnecting)
                DisconnectingOverlay.Opacity = 1;
        }, DispatcherPriority.Render);
    }

    private void Minimize_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Maximize_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var isMaximized = WindowState == WindowState.Maximized;
        WindowState = isMaximized ? WindowState.Normal : WindowState.Maximized;
        MaximizeButton.Content = isMaximized ? "\u25A1" : "\u2750";
        ToolTip.SetTip(MaximizeButton, T(isMaximized ? "common.maximize" : "common.restore", isMaximized ? "Maximize" : "Restore"));
    }

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && WindowState == WindowState.Normal)
            BeginMoveDrag(e);
    }

    private void Resize_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState != WindowState.Normal || sender is not Control { Tag: string edgeName })
            return;

        if (Enum.TryParse<WindowEdge>(edgeName, out var edge))
            BeginResizeDrag(edge, e);
    }

    private void Root_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isFullScreen && !_isPinned && e.GetPosition(this).Y <= 6)
        {
            _hideBarTimer.Stop();
            ConnectionBar.IsVisible = true;
        }
    }

    private void Root_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled)
            return;

        if (_isDisconnecting)
        {
            e.Handled = true;
            return;
        }

        if (_systemUi?.HandleKey(e.Key, e.KeyModifiers) == true)
        {
            e.Handled = true;
            return;
        }

        // This is the final host-level fallback in the routed keyboard chain. A managed
        // application window gets the same key first and can consume it to leave only its
        // own full-screen mode.
        if (e.Key == Key.Escape && _isFullScreen)
        {
            SetFullScreen(false);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.I || e.KeyModifiers != (KeyModifiers.Control | KeyModifiers.Shift))
            return;

        e.Handled = true;
        App.Services.GetRequiredService<NetworkInspectorWindowService>().Open();
    }

    private void ConnectionBar_OnPointerEntered(object? sender, PointerEventArgs e) => _hideBarTimer.Stop();

    private void ConnectionBar_OnPointerExited(object? sender, PointerEventArgs e) => ScheduleConnectionBarHide();

    private void ConnectionDragHandle_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || sender is not Control handle)
            return;

        _isDraggingConnectionBar = true;
        _connectionDragStart = e.GetPosition(this);
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void ConnectionDragHandle_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingConnectionBar || sender is not Control handle || e.Pointer.Captured != handle)
            return;

        var current = e.GetPosition(this);
        _connectionBarOffset += current.X - _connectionDragStart.X;
        _connectionDragStart = current;
        ApplyConnectionBarOffset();
        e.Handled = true;
    }

    private void ConnectionDragHandle_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingConnectionBar || sender is not Control handle)
            return;

        _isDraggingConnectionBar = false;
        if (e.Pointer.Captured == handle)
            e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void ApplyConnectionBarOffset()
    {
        // The bar is centred by layout, so retain only a bounded horizontal translation.
        var maximumOffset = Math.Max(0, (Bounds.Width - ConnectionBar.Bounds.Width) / 2 - 8);
        _connectionBarOffset = Math.Clamp(_connectionBarOffset, -maximumOffset, maximumOffset);
        ConnectionBar.RenderTransform = new TranslateTransform(_connectionBarOffset, 0);
    }

    private void ScheduleConnectionBarHide()
    {
        if (_isFullScreen && !_isPinned)
        {
            _hideBarTimer.Stop();
            _hideBarTimer.Start();
        }
    }

    private void HideConnectionBar()
    {
        _hideBarTimer.Stop();
        if (_isFullScreen && !_isPinned)
        {
            ConnectionBar.IsVisible = false;
            ConnectionInfo.IsVisible = false;
        }
    }

    private void RefreshLocalizedText()
    {
        PinButton.Content = _isPinned ? "●" : "○";
        ToolTip.SetTip(PinButton, T(_isPinned ? "shell.connection_bar.unpin_tooltip" : "shell.connection_bar.pin_tooltip", _isPinned ? "Unpin connection bar" : "Pin connection bar"));
        FullScreenButton.Content = _isFullScreen ? "↙" : "↗";
        ToolTip.SetTip(FullScreenButton, T(_isFullScreen ? "shell.full_screen.exit" : "shell.full_screen.enter_tooltip", _isFullScreen ? "Exit full screen" : "Enter full screen"));
    }

    private string T(string key, string englishFallback) => _localization.Get(key, englishFallback);
}
