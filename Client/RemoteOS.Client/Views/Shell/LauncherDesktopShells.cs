using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Client.Localization;
using Client.Services;
using Client.ViewModels.Shell;
using RemoteOS.Shell;
using VectorPath = Avalonia.Controls.Shapes.Path;

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
    private System.ComponentModel.PropertyChangedEventHandler? _wallpaperChanged;

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
        _wallpaperChanged = (_, args) =>
        {
            if (args.PropertyName is null or nameof(ShellSettings.CurrentWallpaper))
                _root.Background = _vm?.Settings.CurrentWallpaper;
        };
        _vm.Settings.PropertyChanged += _wallpaperChanged;
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
    public ValueTask DisposeAsync()
    {
        if (_vm is not null && _wallpaperChanged is not null)
            _vm.Settings.PropertyChanged -= _wallpaperChanged;
        _wallpaperChanged = null;
        _context = null;
        _vm = null;
        return ValueTask.CompletedTask;
    }

    protected abstract void BuildLayout(DesktopShellViewModel vm);

    protected Control Desktop(DesktopShellViewModel vm)
    {
        var workspace = new Grid { ClipToBounds = true };
        _backdrop.PointerPressed += (_, _) => vm.ClearDesktopSelectionCommand.Execute(null);
        _backdrop.ContextMenu = DesktopContextMenu(vm);
        workspace.KeyBindings.Add(new KeyBinding
        {
            Command = vm.OpenDesktopDisplaySettingsCommand,
            Gesture = KeyGesture.Parse("Ctrl+Shift+D"),
        });
        workspace.Children.Add(_backdrop);
        var icons = new ItemsControl
        {
            Margin = new Thickness(14),
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Vertical }),
            ItemTemplate = new FuncDataTemplate<object>((item, _) => Icon(vm, item)),
        };
        icons.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.DesktopItems)));
        icons.Bind(Visual.IsVisibleProperty, new Binding(nameof(vm.AreDesktopIconsVisible)));
        workspace.Children.Add(icons);
        workspace.Children.Add(_windowHost);
        return workspace;
    }

    private static ContextMenu DesktopContextMenu(DesktopShellViewModel vm)
    {
        var showIcons = new MenuItem
        {
            Header = LocalizedText.Get("shell.desktop.context.show_icons", "Show desktop icons"),
            ToggleType = MenuItemToggleType.CheckBox,
        };
        showIcons.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(vm.AreDesktopIconsVisible)) { Mode = BindingMode.TwoWay });

        var view = new MenuItem { Header = LocalizedText.Get("common.view", "View") };
        view.ItemsSource = new object[] { showIcons };

        return new ContextMenu
        {
            ItemsSource = new object[]
            {
                view,
                new MenuItem { Header = LocalizedText.Get("common.refresh", "Refresh"), Command = vm.RefreshDesktopCommand },
                new MenuItem { Header = LocalizedText.Get("common.paste", "Paste"), Command = vm.PasteDesktopCommand },
                new Separator(),
                new MenuItem
                {
                    Header = LocalizedText.Get("shell.desktop_display.configure_ellipsis", "Configure desktop display..."),
                    Command = vm.OpenDesktopDisplaySettingsCommand,
                    InputGesture = KeyGesture.Parse("Ctrl+Shift+D"),
                },
                new Separator(),
                new MenuItem { Header = LocalizedText.Get("shell.desktop.context.open_folder", "Open desktop folder"), Command = vm.OpenDesktopFolderCommand },
                new MenuItem { Header = LocalizedText.Get("shell.desktop.context.open_explorer", "Open File Explorer"), Command = vm.OpenFileExplorerCommand },
                new MenuItem { Header = LocalizedText.Get("shell.desktop.context.open_terminal", "Open Terminal"), Command = vm.OpenTerminalCommand },
                new Separator(),
                new MenuItem { Header = LocalizedText.Get("settings.page.personalization", "Personalization"), Command = vm.OpenPersonalizationCommand },
            },
        };
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
        stack.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, FontSize = 15, Foreground = Brushes.White });
        var apps = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<AppEntryViewModel>((app, _) => new Button
            {
                Content = app.DisplayName, Command = app.LaunchCommand, HorizontalContentAlignment = HorizontalAlignment.Left,
                Foreground = Brushes.White,
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
        var launcherButton = new Button
        {
            Content = launcherGlyph, Command = vm.ToggleStartCommand, Width = 38, Height = 34,
            Foreground = Brushes.White,
        };
        ToolTip.SetTip(launcherButton, LocalizedText.Get("shell.launcher.applications", "Applications"));
        stack.Children.Add(launcherButton);
        var groups = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<TaskbarGroupViewModel>((group, _) => TaskbarButton(vm, group)),
        };
        groups.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.TaskbarGroups)));
        stack.Children.Add(groups);
        var showDesktopButton = new Button { Content = "⌄", Command = vm.ShowDesktopCommand, Width = 34, Height = 34, Foreground = Brushes.White };
        ToolTip.SetTip(showDesktopButton, LocalizedText.Get("shell.launcher.show_desktop", "Show desktop"));
        stack.Children.Add(showDesktopButton);
        var clock = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        clock.Bind(TextBlock.TextProperty, new Binding(nameof(vm.Clock)));
        stack.Children.Add(clock);
        bar.Child = stack;
        return bar;
    }

    private static Control Icon(DesktopShellViewModel vm, object item)
    {
        var (name, glyph, image) = item switch
        {
            AppEntryViewModel app => (app.DisplayName, app.IconGlyph ?? "◼", app.IconImage),
            DesktopFileEntryViewModel file => (file.DisplayName, file.IconGlyph, null),
            ShortcutEntryViewModel shortcut => (shortcut.DisplayName, shortcut.IconGlyph ?? "↗", null),
            _ => (item.ToString() ?? string.Empty, "◼", null),
        };
        var content = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
        if (image is not null)
            content.Children.Add(new Image { Source = image, Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center });
        else
            content.Children.Add(new TextBlock { Text = glyph, FontSize = 30, HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock
        {
            Text = name, MaxWidth = 108, MaxLines = 2, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var button = new Button { Content = content, Width = 116, Height = 84, Margin = new Thickness(3),
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
                new MenuItem { Header = LocalizedText.Get("common.open", "Open"), Command = vm.OpenDesktopEntryCommand, CommandParameter = fileEntry },
                new MenuItem { Header = LocalizedText.Get("common.copy", "Copy"), Command = vm.CopyDesktopEntryCommand, CommandParameter = fileEntry },
                new MenuItem { Header = LocalizedText.Get("common.delete", "Delete"), Command = vm.DeleteDesktopEntryCommand, CommandParameter = fileEntry },
            };
            button.ContextMenu = menu;
        }
        return button;
    }

    private static Button TaskbarButton(DesktopShellViewModel vm, TaskbarGroupViewModel group)
    {
        var glyph = group.IconGlyph ?? "◼";
        var button = new Button
        {
            Command = vm.ToggleTaskbarGroupCommand, CommandParameter = group,
            Width = 38, Height = 34, Padding = new Thickness(2), Foreground = Brushes.White,
        };
        ToolTip.SetTip(button, group.DisplayName);
        button.Content = group.IconImage is { } image
            ? new Image { Source = image, Width = 22, Height = 22 }
            : new TextBlock { Text = glyph, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center };
        return button;
    }

    private void ReportWorkArea()
    {
        if (_context is null) return;
        var b = _windowHost.Bounds;
        _context.Surfaces.UpdateWorkArea(new RemoteOS.Core.Primitives.Rect(0, 0, b.Width, b.Height));
    }
}

public sealed class WindowsLikeDesktopShell() : LauncherDesktopShellBase(BuiltInShells.Windows)
{
    protected override void BuildLayout(DesktopShellViewModel vm)
    {
        var layout = new WindowsShellLayoutView();
        layout.Compose(Desktop(vm), WindowsTaskbar(vm), WindowsLauncher(vm));
        _root.Children.Add(layout);
    }

    /// <summary>
    /// The Windows launcher intentionally uses a flat, scrollable application list.  StartApps
    /// is the compatible application catalog, so folders and desktop files never leak into it.
    /// </summary>
    private static Control WindowsLauncher(DesktopShellViewModel vm)
    {
        var panel = new Border
        {
            Width = 412,
            MaxHeight = 620,
            Background = new SolidColorBrush(Color.Parse("#F22B2B2B")),
            BorderBrush = new SolidColorBrush(Color.Parse("#66787878")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            IsVisible = false,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 8,
                Blur = 24,
                Color = Color.Parse("#66000000"),
            }),
        };
        panel.Bind(Visual.IsVisibleProperty, new Binding(nameof(vm.IsStartOpen)));

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("52,*") };
        layout.Children.Add(WindowsSystemRail(vm));

        var appList = new ItemsControl
        {
            Margin = new Thickness(12, 12, 8, 12),
            ItemTemplate = new FuncDataTemplate<AppEntryViewModel>((app, _) => WindowsStartAppButton(vm, app)),
        };
        appList.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.StartApps)));
        var scroller = new ScrollViewer
        {
            Content = appList,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetColumn(scroller, 1);
        layout.Children.Add(scroller);
        panel.Child = layout;
        return panel;
    }

    private static Control WindowsSystemRail(DesktopShellViewModel vm)
    {
        var rail = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#33202020")),
            BorderBrush = new SolidColorBrush(Color.Parse("#337A7A7A")),
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
        var actions = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto") };
        var settings = WindowsGlyphButton("⚙", LocalizedText.Get("common.settings", "Settings"), vm.OpenSettingsCommand);
        Grid.SetRow(settings, 1);
        actions.Children.Add(settings);
        var shutdown = WindowsPowerButton(vm.ShutdownCommand);
        Grid.SetRow(shutdown, 2);
        actions.Children.Add(shutdown);
        rail.Child = actions;
        return rail;
    }

    private static Button WindowsStartAppButton(DesktopShellViewModel vm, AppEntryViewModel app)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("40,*"), Height = 46 };
        row.Children.Add(AppIcon(app, 28));
        var name = new TextBlock
        {
            Text = app.DisplayName,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brushes.White,
            FontSize = 13,
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        return new Button
        {
            Content = row,
            Command = vm.LaunchCommand,
            CommandParameter = app.Id,
            Height = 46,
            Padding = new Thickness(8, 0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
    }

    private static Control WindowsTaskbar(DesktopShellViewModel vm)
    {
        var bar = new Border
        {
            Height = 46,
            Background = new SolidColorBrush(Color.Parse("#E6242424")),
            BorderBrush = new SolidColorBrush(Color.Parse("#557A7A7A")),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = -2,
                Blur = 10,
                Color = Color.Parse("#33000000"),
            }),
        };
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("48,*,Auto") };
        var start = WindowsGlyphButton("⊞", LocalizedText.Get("shell.launcher.start", "Start"), vm.ToggleStartCommand);
        layout.Children.Add(start);

        // Only live window groups are shown here.  There is deliberately no search or
        // synthetic notification area until those services expose shell-facing APIs.
        var groups = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal }),
            ItemTemplate = new FuncDataTemplate<TaskbarGroupViewModel>((group, _) => WindowsTaskbarButton(vm, group)),
        };
        groups.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.TaskbarGroups)));
        var runningApps = new ScrollViewer
        {
            Content = groups,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetColumn(runningApps, 1);
        layout.Children.Add(runningApps);

        var systemArea = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,8") };
        var clock = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10, 0, 8, 0),
        };
        var time = new TextBlock { FontSize = 12, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right };
        time.Bind(TextBlock.TextProperty, new Binding(nameof(vm.Clock)));
        var date = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#D9FFFFFF")), HorizontalAlignment = HorizontalAlignment.Right };
        date.Bind(TextBlock.TextProperty, new Binding(nameof(vm.DateText)));
        clock.Children.Add(time);
        clock.Children.Add(date);
        systemArea.Children.Add(clock);
        var showDesktop = new Button
        {
            Command = vm.ShowDesktopCommand,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Padding = new Thickness(0),
        };
        ToolTip.SetTip(showDesktop, LocalizedText.Get("shell.launcher.show_desktop", "Show desktop"));
        Grid.SetColumn(showDesktop, 1);
        systemArea.Children.Add(showDesktop);
        Grid.SetColumn(systemArea, 2);
        layout.Children.Add(systemArea);
        bar.Child = layout;
        return bar;
    }

    private static Button WindowsTaskbarButton(DesktopShellViewModel vm, TaskbarGroupViewModel group)
    {
        var content = new Grid { RowDefinitions = new RowDefinitions("*,3") };
        var icon = AppIcon(group, 23);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(icon);
        var activeIndicator = new Border
        {
            Height = 3,
            Width = 22,
            Background = new SolidColorBrush(Color.Parse("#FF4CC2FF")),
            HorizontalAlignment = HorizontalAlignment.Center,
            IsVisible = group.IsActive,
        };
        activeIndicator.Bind(Visual.IsVisibleProperty, new Binding(nameof(group.IsActive)));
        Grid.SetRow(activeIndicator, 1);
        content.Children.Add(activeIndicator);
        var button = new Button
        {
            Content = content,
            Command = vm.ToggleTaskbarGroupCommand,
            CommandParameter = group,
            Width = 46,
            Height = 46,
            Padding = new Thickness(2),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
        };
        ToolTip.SetTip(button, group.DisplayName);
        return button;
    }

    private static Button WindowsGlyphButton(string glyph, string tooltip, System.Windows.Input.ICommand command)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontSize = 20,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Command = command,
            Width = 48,
            Height = 46,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
        };
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    private static Button WindowsPowerButton(System.Windows.Input.ICommand command)
    {
        // A stroked SVG-style power path avoids font fallback rendering the U+23FB glyph as a box.
        var button = new Button
        {
            Content = ShellIconFactory.Power(Brushes.White, 20),
            Command = command,
            Width = 48,
            Height = 46,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, LocalizedText.Get("shell.launcher.power", "Power"));
        return button;
    }

    private static Control AppIcon(AppEntryViewModel app, double size) => app.IconImage is { } image
        ? new Image { Source = image, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center }
        : new TextBlock { Text = app.IconGlyph ?? "◼", FontSize = size - 4, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };

    private static Control AppIcon(TaskbarGroupViewModel group, double size) => group.IconImage is { } image
        ? new Image { Source = image, Width = size, Height = size }
        : new TextBlock { Text = group.IconGlyph ?? "◼", FontSize = size - 3, Foreground = Brushes.White };
}

public sealed class MacosLikeDesktopShell() : LauncherDesktopShellBase(BuiltInShells.Macos)
{
    protected override void BuildLayout(DesktopShellViewModel vm)
    {
        var layout = new MacosShellLayoutView();
        layout.Compose(MacosMenuBar(vm), Desktop(vm), MacosDock(vm), MacosLaunchpad(vm));
        _root.Children.Add(layout);
    }

    private static Control MacosMenuBar(DesktopShellViewModel vm)
    {
        var bar = new Border
        {
            Height = 28,
            Background = new SolidColorBrush(Color.Parse("#D9F7F8FA")),
            BorderBrush = new SolidColorBrush(Color.Parse("#334D5661")),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var menus = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        menus.Children.Add(MacosMenuButton("●", LocalizedText.Get("shell.launcher.launchpad", "Launchpad"), vm.ToggleStartCommand, bold: true));
        menus.Children.Add(MacosMenuButton("RemoteOS", LocalizedText.Get("shell.launcher.system_settings", "System Settings"), vm.OpenSettingsCommand, bold: true));
        menus.Children.Add(MacosMenuButton(LocalizedText.Get("common.file", "File"), LocalizedText.Get("shell.launcher.open_files", "Open Files"), vm.OpenFileExplorerCommand));
        menus.Children.Add(MacosMenuButton(LocalizedText.Get("common.view", "View"), LocalizedText.Get("shell.launcher.show_desktop", "Show desktop"), vm.ShowDesktopCommand));
        menus.Children.Add(MacosMenuButton(LocalizedText.Get("shell.macos.menu.window", "Window"), LocalizedText.Get("shell.launcher.task_manager", "Task Manager"), vm.OpenTaskManagerCommand));
        menus.Children.Add(new TextBlock { Text = LocalizedText.Get("shell.macos.menu.help", "Help"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0), Foreground = new SolidColorBrush(Color.Parse("#17212B")) });
        layout.Children.Add(menus);

        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        status.Children.Add(MacosStatusButton("⌂", LocalizedText.Get("shell.launcher.show_desktop", "Show desktop"), vm.ShowDesktopCommand));
        status.Children.Add(MacosStatusButton("⌕", LocalizedText.Get("shell.launcher.open_launchpad", "Open Launchpad"), vm.ToggleStartCommand));
        status.Children.Add(MacosStatusButton("⚙", LocalizedText.Get("shell.launcher.system_settings", "System Settings"), vm.OpenSettingsCommand));
        var clock = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#17212B")), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 4, 0) };
        clock.Bind(TextBlock.TextProperty, new Binding(nameof(vm.Clock)));
        var date = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#17212B")), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
        date.Bind(TextBlock.TextProperty, new Binding(nameof(vm.DateText)));
        status.Children.Add(clock);
        status.Children.Add(date);
        status.Children.Add(MacosPowerButton(vm.ShutdownCommand));
        Grid.SetColumn(status, 1);
        layout.Children.Add(status);
        bar.Child = layout;
        return bar;
    }

    private static Control MacosDock(DesktopShellViewModel vm)
    {
        var dock = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#D9F3F5F8")),
            BorderBrush = new SolidColorBrush(Color.Parse("#80868E99")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(17),
            Padding = new Thickness(6),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 5,
                Blur = 16,
                Color = Color.Parse("#55000000"),
            }),
        };
        var apps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        apps.Children.Add(MacosDockButton("▰", LocalizedText.Get("shell.launcher.files", "Files"), vm.OpenFileExplorerCommand));
        apps.Children.Add(MacosDockButton("›_", LocalizedText.Get("shell.launcher.terminal", "Terminal"), vm.OpenTerminalCommand));
        apps.Children.Add(MacosDockButton("▥", LocalizedText.Get("shell.launcher.task_manager", "Task Manager"), vm.OpenTaskManagerCommand));
        apps.Children.Add(MacosDockButton("⚙", LocalizedText.Get("shell.launcher.system_settings", "System Settings"), vm.OpenSettingsCommand));
        apps.Children.Add(new Border { Width = 1, Height = 36, Background = new SolidColorBrush(Color.Parse("#6677818C")), Margin = new Thickness(5, 5) });
        var runningApps = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 }),
            ItemTemplate = new FuncDataTemplate<TaskbarGroupViewModel>((group, _) => MacosRunningApp(vm, group)),
        };
        runningApps.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.TaskbarGroups)));
        apps.Children.Add(runningApps);
        apps.Children.Add(new Border { Width = 1, Height = 36, Background = new SolidColorBrush(Color.Parse("#6677818C")), Margin = new Thickness(5, 5) });
        apps.Children.Add(MacosDockButton("▦", LocalizedText.Get("shell.launcher.launchpad", "Launchpad"), vm.ToggleStartCommand));
        dock.Child = apps;
        return dock;
    }

    private static Control MacosLaunchpad(DesktopShellViewModel vm)
    {
        var overlay = new Border
        {
            IsVisible = false,
            Background = new SolidColorBrush(Color.Parse("#E8202630")),
        };
        overlay.Bind(Visual.IsVisibleProperty, new Binding(nameof(vm.IsStartOpen)));
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 26, Margin = new Thickness(80, 58, 80, 78) };
        var search = new TextBox
        {
            Width = 430,
            Height = 38,
            PlaceholderText = LocalizedText.Get("shell.launcher.search_applications", "Search applications"),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(10),
        };
        search.Bind(TextBox.TextProperty, new Binding(nameof(vm.StartSearchQuery)) { Mode = BindingMode.TwoWay });
        layout.Children.Add(search);
        var apps = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal }),
            ItemTemplate = new FuncDataTemplate<AppEntryViewModel>((app, _) => MacosAppTile(vm, app)),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        apps.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.StartSearchResults)));
        var scroller = new ScrollViewer
        {
            Content = apps,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroller, 1);
        layout.Children.Add(scroller);
        overlay.Child = layout;
        return overlay;
    }

    private static Button MacosAppTile(DesktopShellViewModel vm, AppEntryViewModel app)
    {
        var content = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Width = 106, Height = 108 };
        var icon = MacosIcon(app, 58);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(icon);
        var name = new TextBlock
        {
            Text = app.DisplayName,
            Foreground = Brushes.White,
            FontSize = 12,
            MaxLines = 2,
            MaxWidth = 100,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetRow(name, 1);
        content.Children.Add(name);
        return new Button
        {
            Content = content,
            Width = 116,
            Height = 118,
            Padding = new Thickness(4),
            Margin = new Thickness(4),
            Command = vm.LaunchCommand,
            CommandParameter = app.Id,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
        };
    }

    private static Button MacosRunningApp(DesktopShellViewModel vm, TaskbarGroupViewModel group)
    {
        var icon = MacosIcon(group, 31);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var button = MacosInteractiveButton(icon, vm.ToggleTaskbarGroupCommand, group, 44, group.DisplayName);
        return button;
    }

    private static Button MacosDockButton(string glyph, string tooltip, System.Windows.Input.ICommand command)
        => MacosInteractiveButton(new TextBlock
        {
            Text = glyph,
            FontSize = 23,
            Foreground = new SolidColorBrush(Color.Parse("#17212B")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        }, command, null, 44, tooltip);

    /// <summary>Creates a static macOS Dock button.</summary>
    private static Button MacosInteractiveButton(Control content, System.Windows.Input.ICommand command, object? parameter, double size, string tooltip)
    {
        var button = new Button
        {
            Content = content,
            Command = command,
            CommandParameter = parameter,
            Width = size,
            Height = size,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
        };
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    private static Button MacosMenuButton(string title, string tooltip, System.Windows.Input.ICommand command, bool bold = false)
    {
        var button = new Button
        {
            Content = title,
            Command = command,
            Height = 27,
            Padding = new Thickness(7, 0),
            FontSize = 12,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = new SolidColorBrush(Color.Parse("#17212B")),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    private static Button MacosStatusButton(string glyph, string tooltip, System.Windows.Input.ICommand command)
    {
        var button = new Button
        {
            Content = glyph,
            Command = command,
            Width = 25,
            Height = 27,
            Padding = new Thickness(0),
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#17212B")),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    private static Button MacosPowerButton(System.Windows.Input.ICommand command)
    {
        var button = MacosStatusButton(string.Empty, LocalizedText.Get("shell.launcher.power", "Power"), command);
        button.Content = ShellIconFactory.Power(new SolidColorBrush(Color.Parse("#17212B")), 14);
        return button;
    }

    private static Control MacosIcon(AppEntryViewModel app, double size) => app.IconImage is { } image
        ? new Image { Source = image, Width = size, Height = size }
        : new TextBlock { Text = app.IconGlyph ?? "◼", FontSize = size - 6, Foreground = Brushes.White };

    private static Control MacosIcon(TaskbarGroupViewModel group, double size) => group.IconImage is { } image
        ? new Image { Source = image, Width = size, Height = size }
        : new TextBlock { Text = group.IconGlyph ?? "◼", FontSize = size - 4, Foreground = new SolidColorBrush(Color.Parse("#17212B")) };
}

public sealed class UbuntuLikeDesktopShell() : LauncherDesktopShellBase(BuiltInShells.Ubuntu)
{
    protected override void BuildLayout(DesktopShellViewModel vm)
    {
        var layout = new UbuntuShellLayoutView();
        layout.Compose(UbuntuTopBar(vm), UbuntuDock(vm), Desktop(vm), UbuntuLauncher(vm));
        _root.Children.Add(layout);
    }

    /// <summary>
    /// GNOME-style overview opened from the bottom dock button.  It contains only the existing
    /// compatible application catalog, and LaunchCommand closes the overview after activation.
    /// </summary>
    private static Control UbuntuLauncher(DesktopShellViewModel vm)
    {
        var panel = new Border
        {
            IsVisible = false,
            // The overview deliberately consumes the desktop work area.  It does not reserve
            // space for workspace thumbnails: this shell exposes an application-only search.
            Background = new SolidColorBrush(Color.Parse("#F216181B")),
        };
        panel.Bind(Visual.IsVisibleProperty, new Binding(nameof(vm.IsStartOpen)));

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 26,
            Margin = new Thickness(72, 28, 72, 48),
        };
        var search = new TextBox
        {
            Width = 480,
            Height = 42,
            PlaceholderText = LocalizedText.Get("shell.launcher.search_applications", "Search applications"),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        search.Bind(TextBox.TextProperty, new Binding(nameof(vm.StartSearchQuery)) { Mode = BindingMode.TwoWay });
        layout.Children.Add(search);
        var apps = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal }),
            ItemTemplate = new FuncDataTemplate<AppEntryViewModel>((app, _) => UbuntuAppTile(vm, app)),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        apps.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.StartSearchResults)));
        var scroller = new ScrollViewer
        {
            Content = apps,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroller, 1);
        layout.Children.Add(scroller);
        panel.Child = layout;
        return panel;
    }

    private static Control UbuntuTopBar(DesktopShellViewModel vm)
    {
        var bar = new Border
        {
            Height = 32,
            Background = new SolidColorBrush(Color.Parse("#F0141517")),
            BorderBrush = new SolidColorBrush(Color.Parse("#442F3338")),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
        layout.Children.Add(new TextBlock
        {
            Text = "RemoteOS",
            Margin = new Thickness(12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
        });

        var clock = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var date = new TextBlock { Foreground = Brushes.White, FontSize = 12 };
        date.Bind(TextBlock.TextProperty, new Binding(nameof(vm.DateText)));
        var time = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeight.SemiBold };
        time.Bind(TextBlock.TextProperty, new Binding(nameof(vm.Clock)));
        clock.Children.Add(date);
        clock.Children.Add(time);
        Grid.SetColumn(clock, 1);
        layout.Children.Add(clock);

        // Running applications and the available system actions share one horizontal area at
        // the top right, matching a GNOME status region instead of a second vertical dock.
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 2, Margin = new Thickness(0, 0, 7, 0) };
        var runningApps = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal }),
            ItemTemplate = new FuncDataTemplate<TaskbarGroupViewModel>((group, _) => UbuntuRunningApp(vm, group)),
        };
        runningApps.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.TaskbarGroups)));
        right.Children.Add(runningApps);
        right.Children.Add(UbuntuTopButton("⌂", LocalizedText.Get("shell.launcher.show_desktop", "Show desktop"), vm.ShowDesktopCommand));
        right.Children.Add(UbuntuTopButton("⚙", LocalizedText.Get("common.settings", "Settings"), vm.OpenSettingsCommand));
        right.Children.Add(UbuntuPowerButton(vm.ShutdownCommand));
        Grid.SetColumn(right, 2);
        layout.Children.Add(right);
        bar.Child = layout;
        return bar;
    }

    private static Control UbuntuDock(DesktopShellViewModel vm)
    {
        var dock = new Border
        {
            Width = 60,
            Background = new SolidColorBrush(Color.Parse("#EE121416")),
            BorderBrush = new SolidColorBrush(Color.Parse("#55363A3E")),
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
        var actions = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto") };
        actions.Children.Add(UbuntuDockButton("▰", LocalizedText.Get("shell.launcher.files", "Files"), vm.OpenFileExplorerCommand));
        var terminal = UbuntuDockButton("›_", LocalizedText.Get("shell.launcher.terminal", "Terminal"), vm.OpenTerminalCommand);
        Grid.SetRow(terminal, 1);
        actions.Children.Add(terminal);
        var taskManager = UbuntuDockButton("▥", LocalizedText.Get("shell.launcher.task_manager", "Task Manager"), vm.OpenTaskManagerCommand);
        Grid.SetRow(taskManager, 2);
        actions.Children.Add(taskManager);
        var settings = UbuntuDockButton("⚙", LocalizedText.Get("common.settings", "Settings"), vm.OpenSettingsCommand);
        Grid.SetRow(settings, 3);
        actions.Children.Add(settings);

        var applications = UbuntuDockButton("⠿", LocalizedText.Get("shell.launcher.show_applications", "Show Applications"), vm.ToggleStartCommand, 28);
        Grid.SetRow(applications, 5);
        actions.Children.Add(applications);
        dock.Child = actions;
        return dock;
    }

    private static Button UbuntuAppTile(DesktopShellViewModel vm, AppEntryViewModel app)
    {
        var content = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Width = 118, Height = 108 };
        var icon = UbuntuAppIcon(app, 56);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(icon);
        var name = new TextBlock
        {
            Text = app.DisplayName,
            Foreground = Brushes.White,
            FontSize = 12,
            MaxLines = 2,
            MaxWidth = 110,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetRow(name, 1);
        content.Children.Add(name);
        return new Button
        {
            Content = content,
            Width = 128,
            Height = 120,
            Padding = new Thickness(4),
            Margin = new Thickness(4),
            Command = vm.LaunchCommand,
            CommandParameter = app.Id,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
        };
    }

    private static Button UbuntuRunningApp(DesktopShellViewModel vm, TaskbarGroupViewModel group)
    {
        var icon = UbuntuAppIcon(group, 18);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var button = new Button
        {
            Content = icon,
            Command = vm.ToggleTaskbarGroupCommand,
            CommandParameter = group,
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
        };
        ToolTip.SetTip(button, group.DisplayName);
        return button;
    }

    private static Button UbuntuDockButton(string glyph, string tooltip, System.Windows.Input.ICommand command, double fontSize = 23)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontSize = fontSize,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Command = command,
            Width = 60,
            Height = 52,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
        };
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    private static Button UbuntuTopButton(string glyph, string tooltip, System.Windows.Input.ICommand command)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontSize = 15,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Command = command,
            Width = 28,
            Height = 30,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
        };
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    private static Button UbuntuPowerButton(System.Windows.Input.ICommand command)
    {
        var button = UbuntuTopButton(string.Empty, LocalizedText.Get("shell.launcher.power", "Power"), command);
        button.Content = ShellIconFactory.Power(Brushes.White, 15);
        return button;
    }

    private static Control UbuntuAppIcon(AppEntryViewModel app, double size) => app.IconImage is { } image
        ? new Image { Source = image, Width = size, Height = size }
        : new TextBlock { Text = app.IconGlyph ?? "◼", FontSize = size - 5, Foreground = Brushes.White };

    private static Control UbuntuAppIcon(TaskbarGroupViewModel group, double size) => group.IconImage is { } image
        ? new Image { Source = image, Width = size, Height = size }
        : new TextBlock { Text = group.IconGlyph ?? "◼", FontSize = size - 3, Foreground = Brushes.White };
}

public static class BuiltInShells
{
    public static readonly ShellDescriptor Windows = new("remoteos.windows-like", "Windows-like", "1.0.0", ShellSourceKind.BuiltIn, ShellCapabilities.All);
    public static readonly ShellDescriptor Macos = new("remoteos.macos-like", "macOS-like", "1.0.0", ShellSourceKind.BuiltIn, ShellCapabilities.All);
    public static readonly ShellDescriptor Ubuntu = new("remoteos.ubuntu-like", "Ubuntu-like", "1.0.0", ShellSourceKind.BuiltIn, ShellCapabilities.All);
    public static readonly IReadOnlyList<ShellDescriptor> All = [Windows, Macos, Ubuntu];
}

/// <summary>Shared vector controls for the built-in shell chrome.</summary>
internal static class ShellIconFactory
{
    public static Viewbox Power(IBrush foreground, double size)
    {
        var icon = new VectorPath
        {
            Data = StreamGeometry.Parse("M 12,2 L 12,11 M 7.05,5.05 A 7,7 0 1 0 16.95,5.05"),
            Stroke = foreground,
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
        };
        return new Viewbox { Width = size, Height = size, Child = icon };
    }
}
