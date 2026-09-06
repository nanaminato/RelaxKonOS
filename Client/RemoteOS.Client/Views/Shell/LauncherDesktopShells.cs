using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Client.ViewModels.Shell;
using RemoteOS.Shell;

namespace Client.Views.Shell;

/// <summary>
/// Shared launcher primitives. Each concrete shell builds its own root layout; this type never
/// wraps or instantiates the retired DesktopShellView style-only presentation.
/// </summary>
public abstract class LauncherDesktopShellBase : IDesktopShell
{
    protected readonly Grid _root = new();
    private readonly Canvas _windowHost = new() { ClipToBounds = true };
    private readonly Canvas _fullScreenHost = new() { ClipToBounds = true, IsHitTestVisible = false };
    private readonly Panel _overlayHost = new() { IsHitTestVisible = false };
    private readonly Border _backdrop = new() { Background = Brushes.Transparent };
    private ShellPresentationContext? _context;
    private DesktopShellViewModel? _vm;

    protected LauncherDesktopShellBase(ShellDescriptor descriptor) => Descriptor = descriptor;
    public ShellDescriptor Descriptor { get; }
    public Control View => _root;

    public Task InitializeAsync(ShellPresentationContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _vm = context.State.Snapshot as DesktopShellViewModel
            ?? throw new InvalidOperationException("The client did not publish a desktop workspace state.");
        _root.DataContext = _vm;
        _root.Background = _vm.Settings.CurrentWallpaper;
        BuildLayout(_vm);
        // Full-screen windows and system dialogs must cover every launcher chrome, rather than
        // merely the regular work area that deliberately avoids a taskbar or Dock.
        Grid.SetRowSpan(_fullScreenHost, Math.Max(1, _root.RowDefinitions.Count));
        Grid.SetColumnSpan(_fullScreenHost, Math.Max(1, _root.ColumnDefinitions.Count));
        Grid.SetRowSpan(_overlayHost, Math.Max(1, _root.RowDefinitions.Count));
        Grid.SetColumnSpan(_overlayHost, Math.Max(1, _root.ColumnDefinitions.Count));
        _root.Children.Add(_fullScreenHost);
        _root.Children.Add(_overlayHost);
        _windowHost.SizeChanged += (_, _) => ReportWorkArea();
        _fullScreenHost.SizeChanged += (_, _) => ReportWorkArea();
        context.Surfaces.Register(new ShellSurfaces(_windowHost, _fullScreenHost, _overlayHost, _backdrop));
        return Task.CompletedTask;
    }

    public virtual Task ActivateAsync(CancellationToken cancellationToken)
    {
        ReportWorkArea();
        return Task.CompletedTask;
    }

    public virtual Task DeactivateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected abstract void BuildLayout(DesktopShellViewModel vm);

    protected Control Desktop(DesktopShellViewModel vm)
    {
        var workspace = new Grid { ClipToBounds = true };
        _backdrop.PointerPressed += (_, _) => vm.ClearDesktopSelectionCommand.Execute(null);
        workspace.Children.Add(_backdrop);
        var icons = new ItemsControl
        {
            Margin = new Thickness(14),
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Vertical }),
            ItemTemplate = new FuncDataTemplate<object>((item, _) => Icon(vm, item)),
        };
        icons.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.DesktopItems)));
        workspace.Children.Add(icons);
        workspace.Children.Add(_windowHost);
        return workspace;
    }

    protected Control Launcher(DesktopShellViewModel vm, string label)
    {
        var panel = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#E61A2333")),
            BorderBrush = new SolidColorBrush(Color.Parse("#668BA8C7")),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10), IsVisible = false, Width = 310, MaxHeight = 460,
        };
        panel.Bind(Visual.IsVisibleProperty, new Binding(nameof(vm.IsStartOpen)));
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, FontSize = 15 });
        var apps = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<AppEntryViewModel>((app, _) => new Button
            {
                Content = app.DisplayName, Command = app.LaunchCommand, HorizontalContentAlignment = HorizontalAlignment.Left,
            }),
        };
        apps.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.StartApps)));
        stack.Children.Add(apps); panel.Child = stack;
        return panel;
    }

    protected Control AppBar(DesktopShellViewModel vm, string launcherGlyph, bool vertical = false)
    {
        var bar = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#E617202D")),
            BorderBrush = new SolidColorBrush(Color.Parse("#668BA8C7")), BorderThickness = new Thickness(1),
            Padding = new Thickness(5),
        };
        var stack = new StackPanel { Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal, Spacing = 4 };
        stack.Children.Add(new Button { Content = launcherGlyph, Command = vm.ToggleStartCommand });
        var groups = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<TaskbarGroupViewModel>((group, _) => new Button
            {
                Content = group.DisplayName, Command = vm.ToggleTaskbarGroupCommand, CommandParameter = group,
            }),
        };
        groups.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.TaskbarGroups)));
        stack.Children.Add(groups);
        stack.Children.Add(new Button { Content = "⌄", Command = vm.ShowDesktopCommand });
        var clock = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        clock.Bind(TextBlock.TextProperty, new Binding(nameof(vm.Clock)));
        stack.Children.Add(clock);
        bar.Child = stack;
        return bar;
    }

    private static Control Icon(DesktopShellViewModel vm, object item)
    {
        var name = item switch
        {
            AppEntryViewModel app => app.DisplayName,
            DesktopFileEntryViewModel file => file.DisplayName,
            ShortcutEntryViewModel shortcut => shortcut.DisplayName,
            _ => item.ToString() ?? string.Empty,
        };
        var button = new Button { Content = name, Width = 116, Height = 84, Margin = new Thickness(3),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => vm.SelectDesktopItemCommand.Execute(item);
        button.DoubleTapped += (_, _) =>
        {
            switch (item)
            {
                case AppEntryViewModel app: app.LaunchCommand.Execute(null); break;
                case DesktopFileEntryViewModel file: vm.OpenDesktopEntryCommand.Execute(file); break;
                case ShortcutEntryViewModel shortcut: shortcut.ActivateCommand.Execute(null); break;
            }
        };
        if (item is DesktopFileEntryViewModel fileEntry)
        {
            var menu = new ContextMenu();
            menu.ItemsSource = new object[]
            {
                new MenuItem { Header = "Open", Command = vm.OpenDesktopEntryCommand, CommandParameter = fileEntry },
                new MenuItem { Header = "Copy", Command = vm.CopyDesktopEntryCommand, CommandParameter = fileEntry },
                new MenuItem { Header = "Delete", Command = vm.DeleteDesktopEntryCommand, CommandParameter = fileEntry },
            };
            button.ContextMenu = menu;
        }
        return button;
    }

    private void ReportWorkArea()
    {
        if (_context is null) return;
        var b = _windowHost.Bounds;
        _context.Surfaces.UpdateWorkArea(new RemoteOS.Core.Primitives.Rect(0, 0, b.Width, b.Height));
    }
}

public sealed class RemoteOsDesktopShell() : LauncherDesktopShellBase(BuiltInShells.Default)
{
    protected override void BuildLayout(DesktopShellViewModel vm)
    {
        _root.RowDefinitions = new RowDefinitions("*,Auto");
        var desktop = Desktop(vm); Grid.SetRow(desktop, 0); _root.Children.Add(desktop);
        var bar = AppBar(vm, "⊞"); Grid.SetRow(bar, 1); _root.Children.Add(bar);
        var launcher = Launcher(vm, "RemoteOS applications"); Grid.SetRow(launcher, 0); launcher.HorizontalAlignment = HorizontalAlignment.Left; launcher.VerticalAlignment = VerticalAlignment.Bottom; launcher.Margin = new Thickness(12); _root.Children.Add(launcher);
    }
}

public sealed class WindowsLikeDesktopShell() : LauncherDesktopShellBase(BuiltInShells.Windows)
{
    protected override void BuildLayout(DesktopShellViewModel vm)
    {
        _root.RowDefinitions = new RowDefinitions("*,Auto");
        var desktop = Desktop(vm); Grid.SetRow(desktop, 0); _root.Children.Add(desktop);
        var bar = AppBar(vm, "▣"); Grid.SetRow(bar, 1); _root.Children.Add(bar);
        var launcher = Launcher(vm, "Start / Search"); Grid.SetRow(launcher, 0); launcher.HorizontalAlignment = HorizontalAlignment.Left; launcher.VerticalAlignment = VerticalAlignment.Bottom; launcher.Margin = new Thickness(8); _root.Children.Add(launcher);
    }
}

public sealed class MacosLikeDesktopShell() : LauncherDesktopShellBase(BuiltInShells.Macos)
{
    protected override void BuildLayout(DesktopShellViewModel vm)
    {
        _root.RowDefinitions = new RowDefinitions("*,Auto");
        var desktop = Desktop(vm); Grid.SetRow(desktop, 0); _root.Children.Add(desktop);
        var dock = AppBar(vm, "◉"); Grid.SetRow(dock, 1); dock.HorizontalAlignment = HorizontalAlignment.Center; dock.Margin = new Thickness(0, 0, 0, 10); _root.Children.Add(dock);
        var launcher = Launcher(vm, "Launchpad"); Grid.SetRow(launcher, 0); launcher.HorizontalAlignment = HorizontalAlignment.Center; launcher.VerticalAlignment = VerticalAlignment.Center; _root.Children.Add(launcher);
    }
}

public sealed class UbuntuLikeDesktopShell() : LauncherDesktopShellBase(BuiltInShells.Ubuntu)
{
    protected override void BuildLayout(DesktopShellViewModel vm)
    {
        _root.RowDefinitions = new RowDefinitions("Auto,*"); _root.ColumnDefinitions = new ColumnDefinitions("Auto,*");
        var top = AppBar(vm, "◉"); Grid.SetRow(top, 0); Grid.SetColumnSpan(top, 2); _root.Children.Add(top);
        var dock = AppBar(vm, "▦", vertical: true); Grid.SetRow(dock, 1); _root.Children.Add(dock);
        var desktop = Desktop(vm); Grid.SetRow(desktop, 1); Grid.SetColumn(desktop, 1); _root.Children.Add(desktop);
        var launcher = Launcher(vm, "Applications overview"); Grid.SetRow(launcher, 1); Grid.SetColumn(launcher, 1); launcher.HorizontalAlignment = HorizontalAlignment.Center; launcher.VerticalAlignment = VerticalAlignment.Center; _root.Children.Add(launcher);
    }
}

public static class BuiltInShells
{
    public static readonly ShellDescriptor Default = new("remoteos.default", "RemoteOS", "1.0.0", ShellSourceKind.BuiltIn, ShellCapabilities.All);
    public static readonly ShellDescriptor Windows = new("remoteos.windows-like", "Windows-like", "1.0.0", ShellSourceKind.BuiltIn, ShellCapabilities.All);
    public static readonly ShellDescriptor Macos = new("remoteos.macos-like", "macOS-like", "1.0.0", ShellSourceKind.BuiltIn, ShellCapabilities.All);
    public static readonly ShellDescriptor Ubuntu = new("remoteos.ubuntu-like", "Ubuntu-like", "1.0.0", ShellSourceKind.BuiltIn, ShellCapabilities.All);
    public static readonly IReadOnlyList<ShellDescriptor> All = [Default, Windows, Macos, Ubuntu];
}
