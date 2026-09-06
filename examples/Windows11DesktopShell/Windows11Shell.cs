using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RemoteOS.Shell;

namespace Example.Windows11DesktopShell;

/// <summary>
/// A self-contained external desktop shell. It deliberately references only RemoteOS.Shell and
/// communicates with the client through the public shell actions and surface registry.
/// </summary>
public sealed class Windows11ShellFactory : IDesktopShellFactory
{
    public ShellDescriptor Descriptor { get; } = new(
        "com.example.windows11-desktop",
        "Windows 11 Desktop (External)",
        "1.0.0",
        ShellSourceKind.ExternalPackage,
        ShellCapabilities.Desktop | ShellCapabilities.ShellOverlays,
        "com.example.windows11");

    public IDesktopShell Create() => new Windows11Shell(Descriptor);
}

public sealed class Windows11Shell : IDesktopShell
{
    private const double TaskbarHeight = 52;
    private static readonly IBrush Glass = Brush("#E6F3F6FA");
    private static readonly IBrush GlassBorder = Brush("#99FFFFFF");
    private static readonly IBrush TextPrimary = Brush("#FF202124");
    private static readonly IBrush TextSecondary = Brush("#FF5F6368");
    private static readonly IBrush Accent = Brush("#FF0067C0");

    private readonly Grid _root = new();
    private readonly Canvas _windowHost = new() { ClipToBounds = true };
    private readonly Canvas _fullScreenHost = new() { ClipToBounds = true, IsHitTestVisible = false };
    private readonly Canvas _overlayHost = new() { IsHitTestVisible = false };
    private readonly Border _inputBackdrop = new() { Background = Brushes.Transparent };
    private readonly Grid _startOverlay = new() { IsVisible = false };
    private readonly Grid _quickSettingsOverlay = new() { IsVisible = false };
    private readonly TextBlock _clock = new();
    private readonly TextBlock _date = new();
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private IShellSurfaceRegistry? _surfaces;
    private ShellPresentationContext? _context;

    public Windows11Shell(ShellDescriptor descriptor)
    {
        Descriptor = descriptor;
        _clockTimer.Tick += (_, _) => UpdateClock();
    }

    public ShellDescriptor Descriptor { get; }
    public Control View => _root;

    public Task InitializeAsync(ShellPresentationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _context = context;
        _surfaces = context.Surfaces;

        _root.RowDefinitions = new RowDefinitions("*,Auto");
        _root.Background = CreateWallpaper();

        var desktop = new Grid();
        _inputBackdrop.PointerPressed += (_, _) => CloseFlyouts();
        desktop.Children.Add(_inputBackdrop);
        desktop.Children.Add(BuildDesktopShortcuts(context));
        desktop.Children.Add(_windowHost);
        Grid.SetRow(desktop, 0);
        _root.Children.Add(desktop);

        var taskbar = BuildTaskbar(context);
        Grid.SetRow(taskbar, 1);
        _root.Children.Add(taskbar);

        ConfigureStartMenu(context);
        ConfigureQuickSettings(context);
        AddShellLayer(_startOverlay);
        AddShellLayer(_quickSettingsOverlay);
        AddShellLayer(_fullScreenHost);
        AddShellLayer(_overlayHost);

        _windowHost.SizeChanged += OnWindowHostSizeChanged;
        context.Surfaces.Register(new ShellSurfaces(_windowHost, _fullScreenHost, _overlayHost, _inputBackdrop));
        UpdateClock();
        return Task.CompletedTask;
    }

    public Task ActivateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpdateClock();
        _clockTimer.Start();
        ReportWorkArea();
        return Task.CompletedTask;
    }

    public Task DeactivateAsync(CancellationToken cancellationToken)
    {
        _clockTimer.Stop();
        CloseFlyouts();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _clockTimer.Stop();
        _windowHost.SizeChanged -= OnWindowHostSizeChanged;
        _context = null;
        _surfaces = null;
        return ValueTask.CompletedTask;
    }

    private Control BuildDesktopShortcuts(ShellPresentationContext context)
    {
        var shortcuts = new StackPanel
        {
            Margin = new Thickness(18, 18, 0, 0),
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        shortcuts.Children.Add(DesktopShortcut("▣", "Display settings", () => context.Overlays.ShowDesktopDisplaySettingsAsync()));
        shortcuts.Children.Add(DesktopShortcut("⚙", "Personalization", () =>
        {
            context.Actions.OpenSettings(SettingsRoute.Personalization);
            return Task.CompletedTask;
        }));
        shortcuts.Children.Add(DesktopShortcut("↻", "Refresh desktop", () => context.Actions.RefreshDesktopAsync()));
        return shortcuts;
    }

    private Border BuildTaskbar(ShellPresentationContext context)
    {
        var bar = new Border
        {
            Height = TaskbarHeight,
            Background = Glass,
            BorderBrush = GlassBorder,
            BorderThickness = new Thickness(0, 1, 0, 0),
            BoxShadow = Shadow(0, -3, 18, "#330A3154"),
        };
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };

        var centered = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        centered.Children.Add(TaskbarButton(BuildWindowsLogo(), "Start", ToggleStart));
        centered.Children.Add(TaskbarButton(Glyph("⌕", 27), "Search", ToggleStart));
        centered.Children.Add(TaskbarButton(Glyph("▣", 21), "Task view", context.Actions.ShowDesktop));
        centered.Children.Add(TaskbarButton(Glyph("⚙", 20), "Settings", () => context.Actions.OpenSettings(SettingsRoute.Root)));
        Grid.SetColumn(centered, 1);
        layout.Children.Add(centered);

        var tray = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 8, 0),
        };
        tray.Children.Add(TaskbarButton(Glyph("⌃", 15), "Hidden icons", ToggleQuickSettings, 34));
        tray.Children.Add(TaskbarButton(Glyph("◔  )))", 13), "Quick settings", ToggleQuickSettings, 68));
        var clockButton = new Button
        {
            Content = BuildClock(),
            Command = new ActionCommand(ToggleQuickSettings),
            Width = 76,
            Padding = new Thickness(4, 2),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
        };
        tray.Children.Add(clockButton);
        tray.Children.Add(new Button
        {
            Width = 7,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brush("#557A8792"),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Command = new ActionCommand(context.Actions.ShowDesktop),
        });
        Grid.SetColumn(tray, 2);
        layout.Children.Add(tray);
        bar.Child = layout;
        return bar;
    }

    private void ConfigureStartMenu(ShellPresentationContext context)
    {
        var dismiss = new Border { Background = Brushes.Transparent };
        dismiss.PointerPressed += (_, _) => _startOverlay.IsVisible = false;
        _startOverlay.Children.Add(dismiss);

        var panel = new Border
        {
            Width = 590,
            Height = 610,
            Margin = new Thickness(0, 0, 0, TaskbarHeight + 10),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Glass,
            BorderBrush = GlassBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            BoxShadow = Shadow(0, 10, 40, "#660A3154"),
        };
        panel.PointerPressed += (_, e) => e.Handled = true;
        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        content.Children.Add(new TextBox
        {
            PlaceholderText = "Type here to search",
            Margin = new Thickness(32, 28, 32, 18),
            Height = 38,
            CornerRadius = new CornerRadius(6),
        });

        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(38, 0, 38, 12) };
        heading.Children.Add(Label("Pinned", 15, FontWeight.SemiBold));
        var allApps = new Button { Content = "All apps  ›", Padding = new Thickness(12, 5), Command = new ActionCommand(() => context.Actions.OpenSettings(SettingsRoute.Root)) };
        Grid.SetColumn(allApps, 1);
        heading.Children.Add(allApps);
        Grid.SetRow(heading, 1);
        content.Children.Add(heading);

        var pinned = new UniformGrid { Columns = 3, Rows = 2, Margin = new Thickness(30, 0, 30, 26) };
        pinned.Children.Add(StartTile("⚙", "Settings", () => context.Actions.OpenSettings(SettingsRoute.Root)));
        pinned.Children.Add(StartTile("◩", "Personalization", () => context.Actions.OpenSettings(SettingsRoute.Personalization)));
        pinned.Children.Add(StartTile("▣", "Display", () => _ = context.Overlays.ShowDesktopDisplaySettingsAsync()));
        pinned.Children.Add(StartTile("↻", "Refresh", () => _ = context.Actions.RefreshDesktopAsync()));
        pinned.Children.Add(StartTile("▰", "Show desktop", context.Actions.ShowDesktop));
        pinned.Children.Add(StartTile("?", "About this shell", () => context.Actions.OpenSettings(SettingsRoute.Root)));
        Grid.SetRow(pinned, 2);
        content.Children.Add(pinned);

        var footer = new Border
        {
            Background = Brush("#55FFFFFF"),
            BorderBrush = Brush("#66FFFFFF"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            CornerRadius = new CornerRadius(0, 0, 12, 12),
            Padding = new Thickness(36, 14),
        };
        var footerContent = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        footerContent.Children.Add(new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = Accent,
            Child = new TextBlock { Text = "R", Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        var user = Label("RemoteOS", 13, FontWeight.SemiBold);
        user.VerticalAlignment = VerticalAlignment.Center;
        user.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(user, 1);
        footerContent.Children.Add(user);
        var power = TaskbarButton(Glyph("⏻", 19), "Show desktop", context.Actions.ShowDesktop, 40);
        Grid.SetColumn(power, 2);
        footerContent.Children.Add(power);
        footer.Child = footerContent;
        Grid.SetRow(footer, 3);
        content.Children.Add(footer);
        panel.Child = content;
        _startOverlay.Children.Add(panel);
    }

    private void ConfigureQuickSettings(ShellPresentationContext context)
    {
        var dismiss = new Border { Background = Brushes.Transparent };
        dismiss.PointerPressed += (_, _) => _quickSettingsOverlay.IsVisible = false;
        _quickSettingsOverlay.Children.Add(dismiss);
        var card = new Border
        {
            Width = 340,
            Margin = new Thickness(0, 0, 12, TaskbarHeight + 10),
            Padding = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Glass,
            BorderBrush = GlassBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            BoxShadow = Shadow(0, 8, 30, "#550A3154"),
        };
        card.PointerPressed += (_, e) => e.Handled = true;
        var body = new StackPanel { Spacing = 16 };
        var toggles = new UniformGrid { Columns = 3, Rows = 1 };
        toggles.Children.Add(QuickToggle("Wi-Fi", "◔", true));
        toggles.Children.Add(QuickToggle("Bluetooth", "ᛒ", true));
        toggles.Children.Add(QuickToggle("Focus", "☾", false));
        body.Children.Add(toggles);
        body.Children.Add(SliderRow("☀", 72));
        body.Children.Add(SliderRow("♩", 48));
        var settings = new Button
        {
            Content = "Open settings",
            HorizontalAlignment = HorizontalAlignment.Right,
            Command = new ActionCommand(() => context.Actions.OpenSettings(SettingsRoute.Root)),
        };
        body.Children.Add(settings);
        card.Child = body;
        _quickSettingsOverlay.Children.Add(card);
    }

    private void AddShellLayer(Control layer)
    {
        Grid.SetRowSpan(layer, 2);
        _root.Children.Add(layer);
    }

    private void ToggleStart()
    {
        _quickSettingsOverlay.IsVisible = false;
        _startOverlay.IsVisible = !_startOverlay.IsVisible;
    }

    private void ToggleQuickSettings()
    {
        _startOverlay.IsVisible = false;
        _quickSettingsOverlay.IsVisible = !_quickSettingsOverlay.IsVisible;
    }

    private void CloseFlyouts()
    {
        _startOverlay.IsVisible = false;
        _quickSettingsOverlay.IsVisible = false;
        _context?.Actions.ClearDesktopSelection();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        _clock.Text = now.ToString("HH:mm");
        _date.Text = now.ToString("yyyy/MM/dd");
    }

    private void OnWindowHostSizeChanged(object? sender, SizeChangedEventArgs e) => ReportWorkArea();

    private void ReportWorkArea()
    {
        if (_surfaces is null) return;
        var bounds = _windowHost.Bounds;
        _surfaces.UpdateWorkArea(new RemoteOS.Core.Primitives.Rect(0, 0, bounds.Width, bounds.Height));
    }

    private static Control DesktopShortcut(string glyph, string text, Func<Task> action)
    {
        var content = new StackPanel { Width = 84, Spacing = 3 };
        content.Children.Add(new TextBlock
        {
            Text = glyph,
            FontSize = 34,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        return new Button
        {
            Content = content,
            Width = 92,
            MinHeight = 78,
            Padding = new Thickness(4),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Command = new AsyncActionCommand(action),
        };
    }

    private static Button StartTile(string glyph, string title, Action action) => new()
    {
        Content = new StackPanel
        {
            Spacing = 7,
            Children =
            {
                new TextBlock { Text = glyph, FontSize = 25, Foreground = Accent, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = title, FontSize = 12, Foreground = TextPrimary, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap },
            },
        },
        Margin = new Thickness(4),
        Padding = new Thickness(6, 14),
        Background = Brushes.Transparent,
        BorderBrush = Brushes.Transparent,
        Command = new ActionCommand(action),
    };

    private static Button TaskbarButton(Control content, string tooltip, Action action, double width = 46)
    {
        var button = new Button
        {
            Content = content,
            Width = width,
            Height = 44,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Command = new ActionCommand(action),
        };
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    private StackPanel BuildClock()
    {
        _clock.FontSize = 11;
        _clock.Foreground = TextPrimary;
        _clock.HorizontalAlignment = HorizontalAlignment.Right;
        _date.FontSize = 11;
        _date.Foreground = TextPrimary;
        _date.HorizontalAlignment = HorizontalAlignment.Right;
        return new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _clock, _date } };
    }

    private static Control BuildWindowsLogo()
    {
        var logo = new UniformGrid { Columns = 2, Rows = 2, Width = 20, Height = 20 };
        for (var i = 0; i < 4; i++)
            logo.Children.Add(new Border { Background = Accent, Margin = new Thickness(1) });
        return logo;
    }

    private static TextBlock Glyph(string text, double size) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = TextPrimary,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBlock Label(string text, double size, FontWeight weight) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        Foreground = TextPrimary,
    };

    private static Control QuickToggle(string title, string glyph, bool active)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontSize = 18, Foreground = active ? Brushes.White : TextPrimary },
            Height = 42,
            Margin = new Thickness(4),
            Background = active ? Accent : Brush("#88FFFFFF"),
            BorderBrush = GlassBorder,
            CornerRadius = new CornerRadius(6),
        };
        ToolTip.SetTip(button, title);
        return button;
    }

    private static Control SliderRow(string glyph, double value)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*") };
        row.Children.Add(Glyph(glyph, 17));
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = value, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        return row;
    }

    private static IBrush CreateWallpaper() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#0B72C9"), 0),
            new GradientStop(Color.Parse("#45B8E8"), .42),
            new GradientStop(Color.Parse("#8E58C7"), 1),
        },
    };

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));

    private static BoxShadows Shadow(double x, double y, double blur, string color) => new(new BoxShadow
    {
        OffsetX = x,
        OffsetY = y,
        Blur = blur,
        Color = Color.Parse(color),
    });

    private sealed class ActionCommand(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }

    private sealed class AsyncActionCommand(Func<Task> action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public async void Execute(object? parameter) => await action();
    }
}
