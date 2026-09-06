using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RemoteOS.Shell;

namespace Example.NeonDesktopShell;

/// <summary>Minimal package sample: it references only RemoteOS.Shell and receives no Client service provider.</summary>
public sealed class NeonShellFactory : IDesktopShellFactory
{
    public ShellDescriptor Descriptor { get; } = new("com.example.neon-desktop", "Neon Desktop", "1.0.0",
        ShellSourceKind.ExternalPackage, ShellCapabilities.All, "com.example.neon");
    public IDesktopShell Create() => new NeonShell(Descriptor);
}

public sealed class NeonShell(ShellDescriptor descriptor) : IDesktopShell
{
    private readonly Grid _root = new() { Background = new SolidColorBrush(Color.Parse("#120B29")) };
    private readonly Canvas _windows = new() { ClipToBounds = true };
    private readonly Canvas _fullScreen = new() { ClipToBounds = true, IsHitTestVisible = false };
    private readonly Panel _overlays = new Canvas { IsHitTestVisible = false };
    private readonly Border _backdrop = new() { Background = Brushes.Transparent };
    private IShellSurfaceRegistry? _surfaces;

    public ShellDescriptor Descriptor { get; } = descriptor;
    public Control View => _root;

    public Task InitializeAsync(ShellPresentationContext context, CancellationToken cancellationToken)
    {
        _surfaces = context.Surfaces;
        _root.RowDefinitions = new RowDefinitions("*,Auto");
        var desktop = new Grid();
        _backdrop.PointerPressed += (_, _) => context.Actions.ClearDesktopSelection();
        desktop.Children.Add(_backdrop);
        desktop.Children.Add(new TextBlock { Text = "NEON", FontSize = 42, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#3CF4E6")), Margin = new Thickness(32) });
        desktop.Children.Add(_windows); Grid.SetRow(desktop, 0); _root.Children.Add(desktop);
        var dock = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(10) };
        dock.Children.Add(new Button { Content = "Apps", Command = new ActionCommand(() => context.Actions.OpenSettings(SettingsRoute.Personalization)) });
        dock.Children.Add(new Button { Content = "Show desktop", Command = new ActionCommand(context.Actions.ShowDesktop) });
        var chrome = new Border { Background = new SolidColorBrush(Color.Parse("#E71C1038")), Child = dock };
        Grid.SetRow(chrome, 1); _root.Children.Add(chrome);
        Grid.SetRowSpan(_fullScreen, 2); Grid.SetRowSpan(_overlays, 2); _root.Children.Add(_fullScreen); _root.Children.Add(_overlays);
        _windows.SizeChanged += (_, _) => ReportWorkArea();
        context.Surfaces.Register(new ShellSurfaces(_windows, _fullScreen, _overlays, _backdrop));
        return Task.CompletedTask;
    }

    public Task ActivateAsync(CancellationToken cancellationToken) { ReportWorkArea(); return Task.CompletedTask; }
    public Task DeactivateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private void ReportWorkArea()
    {
        if (_surfaces is null) return;
        var b = _windows.Bounds; _surfaces.UpdateWorkArea(new RemoteOS.Core.Primitives.Rect(0, 0, b.Width, b.Height));
    }

    private sealed class ActionCommand(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
