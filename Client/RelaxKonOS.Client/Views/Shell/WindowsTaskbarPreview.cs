using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Diagnostics;
using RelaxKonOS.Client.ViewModels.Shell;
using RelaxKonOS.UI.Themes;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Views.Shell;

/// <summary>
/// An in-host preview strip: fullscreen windows and system dialogs still outrank it.
/// Hover never changes activation, z-order or the minimized state of an application.
/// </summary>
internal sealed class WindowsTaskbarPreview : IDisposable
{
    private readonly Control _root;
    private readonly ITaskbarPreviewContext _vm;
    private readonly Border _panel;
    // Keep the short trip from the icon to its elevated strip inside the hover region.
    private readonly Border _hoverBridge = new() { Background = Brushes.Transparent, IsVisible = false };
    private readonly ItemsControl _cards;
    private readonly Dictionary<Button, TaskbarGroupViewModel> _buttons = [];
    private readonly DispatcherTimer _open = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _close = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private Button? _pending;
    private Button? _anchor;
    private Window? _window;
    private readonly Stopwatch _traceClock = Stopwatch.StartNew();
    private long _observeUntil;
    private long _lastPointerTrace = -250;
    private long _lastHeartbeat;
    private Rect? _lastPanelPosition;
    private Point? _pointerPosition;
    private bool _pointerPositionKnown;

    public Canvas Host { get; } = new() { ClipToBounds = true };

    public WindowsTaskbarPreview(Control root, ITaskbarPreviewContext vm)
    {
        _root = root;
        _vm = vm;
        _cards = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }),
            ItemTemplate = new FuncDataTemplate<ManagedWindow>((window, _) => CreateCard(window)),
        };
        _panel = new Border
        {
            IsVisible = false,
            Padding = new Thickness(8),
            Child = new ScrollViewer
            {
                Content = _cards,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
        };
        ThemeResources.BindSurface(_panel, "SurfaceRaisedBrush", borderKey: "BorderDefaultBrush",
            borderThicknessKey: "ControlBorderThickness", cornerRadiusKey: "OverlayCornerRadius");
        ThemeResources.Bind(_panel, Border.BoxShadowProperty, "WindowShadow");
        Host.Children.Add(_hoverBridge);
        Host.Children.Add(_panel);
        _hoverBridge.PointerEntered += OnPanelEntered;
        _hoverBridge.PointerExited += OnPanelExited;
        _panel.PointerEntered += OnPanelEntered;
        _panel.PointerExited += OnPanelExited;
        _panel.SizeChanged += OnHostSizeChanged;
        _open.Tick += OnOpen;
        _close.Tick += OnClose;
        _refresh.Tick += OnRefresh;
        _vm.PropertyChanged += OnStateChanged;
        _root.AddHandler(InputElement.PointerPressedEvent, OnRootPressed, RoutingStrategies.Tunnel);
        _root.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Host.SizeChanged += OnHostSizeChanged;
        Host.AttachedToVisualTree += OnAttached;
        Host.DetachedFromVisualTree += OnDetached;
        Trace("view.created");
    }

    public void Register(Button button, TaskbarGroupViewModel group)
    {
        _buttons[button] = group;
        Trace("icon.registered", new { app = group.AppId.ToString(), button = Describe(button) });
        button.AttachedToVisualTree += (_, _) =>
        {
            _buttons[button] = group;
            Trace("icon.attached", new { app = group.AppId.ToString(), button = Describe(button) });
        };
        button.DetachedFromVisualTree += (_, _) =>
        {
            Trace("icon.detached", new { app = group.AppId.ToString(), button = Describe(button) });
            _buttons.Remove(button);
            if (ReferenceEquals(_pending, button) || ReferenceEquals(_anchor, button)) Dismiss("anchor.detached");
        };
        button.PointerEntered += (_, args) =>
        {
            TracePointer("icon.enter", args, new { app = group.AppId.ToString(), button = Describe(button) });
            _close.Stop();
            _open.Stop();
            _pending = button;
            if (_vm.OpenTaskbarGroup is not null) OpenPending();
            else _open.Start();
            Trace("open.scheduled");
        };
        button.PointerExited += (_, args) =>
        {
            TracePointer("icon.exit", args, new { app = group.AppId.ToString(), button = Describe(button), button.IsPointerOver });
            // A delayed leave from a previous preview must not cancel a fresh re-entry.
            if (button.IsPointerOver) { Trace("icon.exit.ignored"); return; }
            if (ReferenceEquals(_pending, button))
            {
                _pending = null;
                _open.Stop();
            }
            ScheduleClose("icon.exit");
        };
        // Pointer presses identify the clicked anchor even when the pointer did not hover first.
        button.AddHandler(InputElement.PointerPressedEvent, (_, args) =>
        {
            if (!args.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
            TracePointer("icon.press", args, new { app = group.AppId.ToString(), button = Describe(button) });
            _open.Stop();
            _close.Stop();
            _pending = null;
            _anchor = button;
        }, RoutingStrategies.Tunnel);
        button.Click += (_, _) => Trace("icon.click", new { app = group.AppId.ToString(), button = Describe(button) });
    }

    private Control CreateCard(ManagedWindow window)
    {
        var image = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(6) };
        image.Bind(Image.SourceProperty, new Binding("Thumbnail.Image") { Source = window });
        image.Bind(Visual.IsVisibleProperty, new Binding("Thumbnail.HasImage") { Source = window });
        var fallback = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        fallback.Bind(Visual.IsVisibleProperty, new Binding("!Thumbnail.HasImage") { Source = window });
        var icon = new Image { Width = 40, Height = 40 };
        icon.Bind(Image.SourceProperty, new Binding(nameof(window.IconImage)) { Source = window });
        icon.Bind(Visual.IsVisibleProperty, new Binding(nameof(window.HasIconImage)) { Source = window });
        var glyph = new TextBlock { FontSize = 34, HorizontalAlignment = HorizontalAlignment.Center };
        glyph.Bind(TextBlock.TextProperty, new Binding(nameof(window.IconGlyph)) { Source = window });
        glyph.Bind(Visual.IsVisibleProperty, new Binding("!HasIconImage") { Source = window });
        ThemeResources.Bind(glyph, TextBlock.ForegroundProperty, "TextSecondaryBrush");
        fallback.Children.Add(icon);
        fallback.Children.Add(glyph);
        var content = new Grid();
        content.Children.Add(image);
        content.Children.Add(fallback);
        var activate = new Button
        {
            Content = content,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Command = _vm.ActivateTaskbarWindowCommand,
            CommandParameter = window,
        };
        activate.Bind(AutomationProperties.NameProperty, new Binding(nameof(window.Title)) { Source = window });
        ObserveCardButton(activate, "activate", window);

        var title = new TextBlock
        {
            FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0),
        };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(window.Title)) { Source = window });
        ThemeResources.Bind(title, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var close = new Button
        {
            Content = "×", Width = 30, Height = 30, Padding = new Thickness(0),
            Command = _vm.CloseTaskbarWindowCommand, CommandParameter = window,
        };
        var closeLabel = LocalizedText.Get("shell.overview.close", "Close window");
        AutomationProperties.SetName(close, closeLabel);
        ToolTip.SetTip(close, closeLabel);
        ObserveCardButton(close, "close", window);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(title);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        var card = new Grid { Width = 220, Height = 166, RowDefinitions = new RowDefinitions("30,*") };
        card.Children.Add(header);
        Grid.SetRow(activate, 1);
        card.Children.Add(activate);
        Trace("card.created", new { window = window.Info.Id.ToString(), app = window.Info.OwnerAppId.ToString(), card = Describe(card) });
        card.AttachedToVisualTree += (_, _) => Trace("card.attached", new { window = window.Info.Id.ToString(), card = Describe(card) });
        card.DetachedFromVisualTree += (_, _) => Trace("card.detached", new { window = window.Info.Id.ToString(), card = Describe(card) });
        return card;
    }

    private void ObserveCardButton(Button button, string action, ManagedWindow window)
    {
        object Details() => new { action, window = window.Info.Id.ToString(), button = Describe(button), window.IsActive, state = window.State.ToString() };
        button.AddHandler(InputElement.PointerPressedEvent, (_, e) => TracePointer("card.press", e, Details()), RoutingStrategies.Tunnel, handledEventsToo: true);
        button.AddHandler(InputElement.PointerReleasedEvent, (_, e) => TracePointer("card.release", e, Details()), RoutingStrategies.Tunnel, handledEventsToo: true);
        button.PointerCaptureLost += (_, e) => Trace("card.captureLost", new { details = Details(), captured = Describe(e.Pointer.Captured) });
        button.Click += (_, _) => Trace("card.click", Details());
    }

    private void OnOpen(object? sender, EventArgs e) => OpenPending();

    private void OpenPending()
    {
        _open.Stop();
        Trace("open.timerOrSwitch");
        if (_pending is not { } button || !_buttons.TryGetValue(button, out var group)) { Trace("open.cancelled.noAnchor"); return; }
        _pending = null;
        _close.Stop();
        _anchor = button;
        _vm.ShowTaskbarPreview(group);
        Trace("open.completed", new { requestedApp = group.AppId.ToString() });
        Position();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(_vm.IsStartOpen)) Trace("start.changed", new { _vm.IsStartOpen });
        if (e.PropertyName == nameof(_vm.IsStartOpen) && _vm.IsStartOpen) Dismiss("start.open");
        if (e.PropertyName != nameof(_vm.OpenTaskbarGroup)) return;
        Trace("group.changed.begin");
        var group = _vm.OpenTaskbarGroup;
        if (group is null)
        {
            ResetTimers();
            _anchor = null;
            _panel.IsVisible = false;
            _hoverBridge.IsVisible = false;
            _cards.ItemsSource = null;
            Trace("group.changed.hidden");
            return;
        }
        _open.Stop();
        _close.Stop();
        _pending = null;
        _panel.IsVisible = true;
        _hoverBridge.IsVisible = true;
        _cards.ItemsSource = group.Windows;
        if (_anchor is null || !_buttons.TryGetValue(_anchor, out var anchored) || !ReferenceEquals(anchored, group))
            _anchor = _buttons.FirstOrDefault(pair => ReferenceEquals(pair.Value, group)).Key;
        RefreshThumbnails();
        _refresh.Start();
        Position();
        Trace("group.changed.shown");
    }

    private void Position()
    {
        if (!_panel.IsVisible || _anchor?.TranslatePoint(default, Host) is not { } point) return;
        _panel.MaxWidth = Math.Max(1, Host.Bounds.Width - 16);
        _panel.Measure(new Size(_panel.MaxWidth, double.PositiveInfinity));
        var width = _panel.DesiredSize.Width;
        Canvas.SetLeft(_panel, Math.Clamp(point.X + _anchor.Bounds.Width / 2 - width / 2,
            8, Math.Max(8, Host.Bounds.Width - width - 8)));
        var top = Math.Max(8, point.Y - _panel.DesiredSize.Height - 6);
        Canvas.SetTop(_panel, top);
        Canvas.SetLeft(_hoverBridge, point.X);
        Canvas.SetTop(_hoverBridge, top + _panel.DesiredSize.Height);
        _hoverBridge.Width = _anchor.Bounds.Width;
        _hoverBridge.Height = Math.Max(0, point.Y - top - _panel.DesiredSize.Height);
        var position = new Rect(Canvas.GetLeft(_panel), top, width, _panel.DesiredSize.Height);
        if (_lastPanelPosition != position)
        {
            _lastPanelPosition = position;
            Trace("layout.position", new { x = position.X, y = position.Y, width, height = position.Height, anchorX = point.X, anchorY = point.Y });
        }
    }

    private void RefreshThumbnails()
    {
        if (_vm.OpenTaskbarGroup is not { } group) return;
        foreach (var window in group.Windows) window.Thumbnail.Refresh();
    }

    private void OnRefresh(object? sender, EventArgs e)
    {
        RefreshThumbnails();
        Position();
        if (_traceClock.ElapsedMilliseconds - _lastHeartbeat >= 2000)
        {
            _lastHeartbeat = _traceClock.ElapsedMilliseconds;
            Trace("preview.heartbeat", observe: false);
        }
    }

    private void OnPanelEntered(object? sender, PointerEventArgs e)
    {
        TracePointer("panel.enter", e, new { region = Describe(sender) });
        _close.Stop();
    }
    private void OnPanelExited(object? sender, PointerEventArgs e)
    {
        TracePointer("panel.exit", e, new { region = Describe(sender) });
        ScheduleClose("panel.exit");
    }

    private void ScheduleClose(string reason)
    {
        // Hidden cards can emit leave notifications after an activation has dismissed them.
        // They must not cancel the next application's pending hover/open timer.
        if (_vm.OpenTaskbarGroup is not null && _panel.IsVisible) _close.Start();
        Trace("close.scheduledOrIgnored", new { reason });
    }
    private void OnClose(object? sender, EventArgs e)
    {
        _close.Stop();
        Trace("close.timer");
        // Native pointer delivery can report a parent as exited while its image is still
        // under the pointer. Use the last host input position, not that stale hover flag.
        if (IsPointerInHoverRegion()) { Trace("close.cancelled.hovered"); return; }
        Dismiss("hover.timeout");
    }

    private bool IsPointerInHoverRegion()
    {
        if (!_pointerPositionKnown)
            return _panel.IsPointerOver || _hoverBridge.IsPointerOver || _anchor?.IsPointerOver == true || _pending?.IsPointerOver == true;
        if (_pointerPosition is not { } point) return false;
        return ContainsPointer(_panel, point) || ContainsPointer(_hoverBridge, point)
            || ContainsPointer(_anchor, point) || ContainsPointer(_pending, point);
    }

    private bool ContainsPointer(Control? control, Point point)
        => control is { IsEffectivelyVisible: true }
            && control.TranslatePoint(default, Host) is { } origin
            && new Rect(origin, control.Bounds.Size).Contains(point);
    private void OnHostSizeChanged(object? sender, SizeChangedEventArgs e) => Position();

    private void OnRootPressed(object? sender, PointerPressedEventArgs e)
    {
        TracePointer("root.press", e);
        for (var current = e.Source as Visual; current is not null; current = current.GetVisualParent())
            if (ReferenceEquals(current, _panel) || current is Button button && _buttons.ContainsKey(button)) return;
        Dismiss("outside.press");
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_panel.IsVisible) return;
        Dismiss("key.escape");
        e.Handled = true;
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _window = TopLevel.GetTopLevel(Host) as Window;
        if (_window is not null)
        {
            _window.Deactivated += OnDeactivated;
            _window.Activated += OnActivated;
            _window.PointerExited += OnWindowPointerExited;
            _window.AddHandler(InputElement.PointerMovedEvent, OnWindowPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            _window.AddHandler(InputElement.PointerPressedEvent, OnWindowPointerButton, RoutingStrategies.Tunnel, handledEventsToo: true);
            _window.AddHandler(InputElement.PointerReleasedEvent, OnWindowPointerButton, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        Trace("view.attached");
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Trace("view.detached");
        UnsubscribeWindow();
        _window = null;
        Dismiss("view.detached");
    }

    private void OnActivated(object? sender, EventArgs e) => Trace("host.activated");
    private void OnDeactivated(object? sender, EventArgs e) => Dismiss("host.deactivated");

    public void Dismiss(string reason = "shell.deactivate")
    {
        Trace("dismiss.begin", new { reason });
        ResetTimers();
        _anchor = null;
        _vm.CloseTaskbarPreviewCommand.Execute(null);
        Trace("dismiss.end", new { reason });
    }

    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        RememberPointer(e);
        if (_panel.IsVisible)
        {
            if (IsPointerInHoverRegion()) _close.Stop();
            else if (!_close.IsEnabled) ScheduleClose("pointer.outside");
        }
        var now = _traceClock.ElapsedMilliseconds;
        if ((_pending is null && !_panel.IsVisible && now > _observeUntil) || now - _lastPointerTrace < 250) return;
        _lastPointerTrace = now;
        TracePointer("host.pointerMove", e, observe: false);
    }

    private void OnWindowPointerButton(object? sender, PointerEventArgs e)
    {
        RememberPointer(e);
        if (_pending is null && !_panel.IsVisible && _traceClock.ElapsedMilliseconds > _observeUntil) return;
        TracePointer(e is PointerPressedEventArgs ? "host.press" : "host.release", e);
    }

    private void RememberPointer(PointerEventArgs e)
    {
        _pointerPositionKnown = true;
        _pointerPosition = e.GetPosition(Host);
    }

    private void OnWindowPointerExited(object? sender, PointerEventArgs e)
    {
        // A native leave can carry the last in-window coordinates; don't keep that
        // position alive. A subsequent in-window move will establish it again.
        _pointerPositionKnown = true;
        _pointerPosition = null;
        TracePointer("host.pointerExit", e);
        ScheduleClose("host.pointerExit");
    }

    private static string? Describe(object? value) => value is null ? null
        : $"{value.GetType().Name}#{RuntimeHelpers.GetHashCode(value):x}";

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;

    private string? AppFor(Button? button) => button is not null && _buttons.TryGetValue(button, out var group) ? group.AppId.ToString() : null;

    private void TracePointer(string eventName, PointerEventArgs e, object? details = null, bool observe = true)
    {
        var point = e.GetPosition(Host);
        var ancestry = e.Source is Visual source ? source.GetVisualAncestors().Prepend(source).ToArray() : null;
        Trace(eventName, new
        {
            x = Finite(point.X), y = Finite(point.Y), source = Describe(e.Source),
            sourcePath = ancestry?.Take(10).Select(Describe).ToArray(),
            sourceInPanel = ancestry?.Contains(_panel),
            captured = Describe(e.Pointer.Captured), e.Handled, details,
        }, observe);
    }

    private void Trace(string eventName, object? details = null, bool observe = true)
    {
        if (observe) _observeUntil = _traceClock.ElapsedMilliseconds + 10000;
        TaskbarPreviewDiagnostics.Record(eventName, new
        {
            view = Describe(this),
            group = _vm.OpenTaskbarGroup?.AppId.ToString(),
            windows = _vm.OpenTaskbarGroup?.WindowCount,
            visible = _panel.IsVisible,
            pending = Describe(_pending), pendingApp = AppFor(_pending),
            anchor = Describe(_anchor), anchorApp = AppFor(_anchor),
            timers = new { open = _open.IsEnabled, close = _close.IsEnabled, refresh = _refresh.IsEnabled },
            hovered = new { panel = _panel.IsPointerOver, bridge = _hoverBridge.IsPointerOver, anchor = _anchor?.IsPointerOver, pending = _pending?.IsPointerOver },
            pointer = new { known = _pointerPositionKnown, x = _pointerPosition?.X, y = _pointerPosition?.Y, inside = IsPointerInHoverRegion() },
            host = new { _root.IsVisible, _root.IsHitTestVisible, _root.IsEnabled, active = _window?.IsActive, scale = _window?.RenderScaling, focus = Describe(_window?.FocusManager?.GetFocusedElement()) },
            panel = new { x = _lastPanelPosition?.X, y = _lastPanelPosition?.Y, width = _panel.Bounds.Width, height = _panel.Bounds.Height },
            bridge = new { x = Finite(Canvas.GetLeft(_hoverBridge)), y = Finite(Canvas.GetTop(_hoverBridge)), width = _hoverBridge.Bounds.Width, height = _hoverBridge.Bounds.Height },
            details,
        });
    }

    private void UnsubscribeWindow()
    {
        if (_window is null) return;
        _window.Deactivated -= OnDeactivated;
        _window.Activated -= OnActivated;
        _window.PointerExited -= OnWindowPointerExited;
        _window.RemoveHandler(InputElement.PointerMovedEvent, OnWindowPointerMoved);
        _window.RemoveHandler(InputElement.PointerPressedEvent, OnWindowPointerButton);
        _window.RemoveHandler(InputElement.PointerReleasedEvent, OnWindowPointerButton);
    }

    private void ResetTimers()
    {
        _open.Stop();
        _close.Stop();
        _refresh.Stop();
        _pending = null;
    }

    public void Dispose()
    {
        Dismiss("view.dispose");
        UnsubscribeWindow();
        _vm.PropertyChanged -= OnStateChanged;
        _hoverBridge.PointerEntered -= OnPanelEntered;
        _hoverBridge.PointerExited -= OnPanelExited;
        _panel.PointerEntered -= OnPanelEntered;
        _panel.PointerExited -= OnPanelExited;
        _root.RemoveHandler(InputElement.PointerPressedEvent, OnRootPressed);
        _root.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        Host.SizeChanged -= OnHostSizeChanged;
        _panel.SizeChanged -= OnHostSizeChanged;
        Host.AttachedToVisualTree -= OnAttached;
        Host.DetachedFromVisualTree -= OnDetached;
        _open.Tick -= OnOpen;
        _close.Tick -= OnClose;
        _refresh.Tick -= OnRefresh;
        _buttons.Clear();
    }
}
