using System.ComponentModel;
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
    private readonly ItemsControl _cards;
    private readonly Dictionary<Button, TaskbarGroupViewModel> _buttons = [];
    private readonly DispatcherTimer _open = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _close = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private Button? _pending;
    private Button? _anchor;
    private Window? _window;

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
        Host.Children.Add(_panel);
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
    }

    public void Register(Button button, TaskbarGroupViewModel group)
    {
        _buttons[button] = group;
        button.AttachedToVisualTree += (_, _) => _buttons[button] = group;
        button.DetachedFromVisualTree += (_, _) =>
        {
            _buttons.Remove(button);
            if (ReferenceEquals(_pending, button) || ReferenceEquals(_anchor, button)) Dismiss();
        };
        button.PointerEntered += (_, _) =>
        {
            _close.Stop();
            _open.Stop();
            _pending = button;
            if (_vm.OpenTaskbarGroup is not null) OpenPending();
            else _open.Start();
        };
        button.PointerExited += (_, _) =>
        {
            if (ReferenceEquals(_pending, button))
            {
                _pending = null;
                _open.Stop();
            }
            _close.Start();
        };
        // Pointer presses identify the clicked anchor even when the pointer did not hover first.
        button.PointerPressed += (_, _) => _anchor = button;
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
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(title);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        var card = new Grid { Width = 220, Height = 166, RowDefinitions = new RowDefinitions("30,*") };
        card.Children.Add(header);
        Grid.SetRow(activate, 1);
        card.Children.Add(activate);
        return card;
    }

    private void OnOpen(object? sender, EventArgs e) => OpenPending();

    private void OpenPending()
    {
        _open.Stop();
        if (_pending is not { } button || !_buttons.TryGetValue(button, out var group)) return;
        _anchor = button;
        _vm.ShowTaskbarPreview(group);
        Position();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(_vm.IsStartOpen) && _vm.IsStartOpen) Dismiss();
        if (e.PropertyName != nameof(_vm.OpenTaskbarGroup)) return;
        var group = _vm.OpenTaskbarGroup;
        _panel.IsVisible = group is not null;
        _cards.ItemsSource = group?.Windows;
        if (group is null)
        {
            _refresh.Stop();
            return;
        }
        if (_anchor is null || !_buttons.TryGetValue(_anchor, out var anchored) || !ReferenceEquals(anchored, group))
            _anchor = _buttons.FirstOrDefault(pair => ReferenceEquals(pair.Value, group)).Key;
        RefreshThumbnails();
        _refresh.Start();
        Position();
    }

    private void Position()
    {
        if (!_panel.IsVisible || _anchor?.TranslatePoint(default, Host) is not { } point) return;
        _panel.MaxWidth = Math.Max(1, Host.Bounds.Width - 16);
        _panel.Measure(new Size(_panel.MaxWidth, double.PositiveInfinity));
        var width = _panel.DesiredSize.Width;
        Canvas.SetLeft(_panel, Math.Clamp(point.X + _anchor.Bounds.Width / 2 - width / 2,
            8, Math.Max(8, Host.Bounds.Width - width - 8)));
        Canvas.SetTop(_panel, Math.Max(8, point.Y - _panel.DesiredSize.Height - 6));
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
    }

    private void OnPanelEntered(object? sender, PointerEventArgs e) => _close.Stop();
    private void OnPanelExited(object? sender, PointerEventArgs e) => _close.Start();
    private void OnClose(object? sender, EventArgs e) => Dismiss();
    private void OnHostSizeChanged(object? sender, SizeChangedEventArgs e) => Position();

    private void OnRootPressed(object? sender, PointerPressedEventArgs e)
    {
        for (var current = e.Source as Visual; current is not null; current = current.GetVisualParent())
            if (ReferenceEquals(current, _panel) || current is Button button && _buttons.ContainsKey(button)) return;
        Dismiss();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_panel.IsVisible) return;
        Dismiss();
        e.Handled = true;
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _window = TopLevel.GetTopLevel(Host) as Window;
        if (_window is not null) _window.Deactivated += OnDeactivated;
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null) _window.Deactivated -= OnDeactivated;
        _window = null;
        Dismiss();
    }

    private void OnDeactivated(object? sender, EventArgs e) => Dismiss();

    public void Dismiss()
    {
        _open.Stop();
        _close.Stop();
        _refresh.Stop();
        _pending = null;
        _vm.CloseTaskbarPreviewCommand.Execute(null);
    }

    public void Dispose()
    {
        Dismiss();
        if (_window is not null) _window.Deactivated -= OnDeactivated;
        _vm.PropertyChanged -= OnStateChanged;
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
