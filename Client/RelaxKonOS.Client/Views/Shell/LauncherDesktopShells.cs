using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Shell;
using RelaxKonOS.WindowManager;
using RelaxKonOS.UI.Themes;
using VectorPath = Avalonia.Controls.Shapes.Path;
using System.Globalization;

namespace RelaxKonOS.Client.Views.Shell;

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
    private LocalizationService? _localization;
    private EventHandler<SystemLanguageChangedEventArgs>? _languageChanged;
    private Window? _topLevel;
    private static readonly object DesktopEntryMarker = new();

    /// <summary>
    /// Desktop icon names are narrow, so a long name is shortened with a character-based trailing
    /// ellipsis. The tooltip always carries the untouched name, so nothing is silently lost.
    /// </summary>
    private static readonly TextTrimming DesktopNameTrimming = new TextTrailingTrimming("…", isWordBased: false);

    protected LauncherDesktopShellBase(ShellDescriptor descriptor) => Descriptor = descriptor;
    public ShellDescriptor Descriptor { get; }
    public Control View => _root;

    public Task InitializeAsync(ShellPresentationContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _vm = context.State.Snapshot as DesktopShellViewModel
            ?? throw new InvalidOperationException("The client did not publish a desktop workspace state.");
        _root.DataContext = _vm;
        _root.PointerPressed += OnRootPointerPressed;
        _root.Background = _vm.Settings.CurrentWallpaper;
        _wallpaperChanged = (_, args) =>
        {
            if (args.PropertyName is null or nameof(ShellSettings.CurrentWallpaper))
                _root.Background = _vm?.Settings.CurrentWallpaper;
        };
        _vm.Settings.PropertyChanged += _wallpaperChanged;
        _localization = App.Services.GetRequiredService<LocalizationService>();
        _languageChanged = (_, _) =>
        {
            if (_vm is not null)
                _backdrop.ContextMenu = CreateDesktopContextMenu(_vm);
        };
        _localization.LanguageChanged += _languageChanged;
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
        TrackTopLevelActivation();
        ReportWorkArea();
        return Task.CompletedTask;
    }

    public virtual Task DeactivateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public ValueTask DisposeAsync()
    {
        if (_vm is not null && _wallpaperChanged is not null)
            _vm.Settings.PropertyChanged -= _wallpaperChanged;
        if (_localization is not null && _languageChanged is not null)
            _localization.LanguageChanged -= _languageChanged;
        _root.PointerPressed -= OnRootPointerPressed;
        if (_topLevel is not null) _topLevel.Deactivated -= OnTopLevelDeactivated;
        _topLevel = null;
        _wallpaperChanged = null;
        _languageChanged = null;
        _localization = null;
        _context = null;
        _vm = null;
        return ValueTask.CompletedTask;
    }

    protected abstract void BuildLayout(DesktopShellViewModel vm);

    /// <summary>
    /// Shell-facing host commands, available once the shell has been initialized. A shell may only
    /// ask the host to act; it can never reach the window manager or the system UI layer directly.
    /// </summary>
    protected IShellActions? Actions => _context?.Actions;

    protected Control Desktop(DesktopShellViewModel vm)
    {
        var workspace = new Grid { ClipToBounds = true };
        _backdrop.ContextMenu = CreateDesktopContextMenu(vm);
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

    /// <summary>
    /// Default desktop context menu, shared by shells that do not provide a platform-specific
    /// interaction model. Built-in shells may override this while retaining the same commands.
    /// </summary>
    protected virtual ContextMenu CreateDesktopContextMenu(DesktopShellViewModel vm)
    {
        var view = new MenuItem { Header = LocalizedText.Get("common.view", "View") };
        view.ItemsSource = new object[] { DesktopIconsToggle(vm) };

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

    protected static MenuItem DesktopIconsToggle(DesktopShellViewModel vm)
    {
        var showIcons = new MenuItem
        {
            Header = LocalizedText.Get("shell.desktop.context.show_icons", "Show desktop icons"),
            ToggleType = MenuItemToggleType.CheckBox,
        };
        // Context menus are detached from the visual tree, so this must use an explicit source
        // rather than relying on inherited DataContext. Otherwise the menu can hide icons but
        // cannot reliably turn them back on.
        showIcons.Bind(MenuItem.IsCheckedProperty, new Binding(nameof(vm.AreDesktopIconsVisible))
        {
            Source = vm,
            Mode = BindingMode.TwoWay,
        });
        return showIcons;
    }

    protected Control Launcher(DesktopShellViewModel vm, string label)
    {
        var panel = new Border
        {
            Padding = new Thickness(10), IsVisible = false, Width = 310, MaxHeight = 460,
        };
        ThemeResources.BindSurface(panel, "SurfaceRaisedBrush", borderKey: "BorderDefaultBrush",
            borderThicknessKey: "ControlBorderThickness", cornerRadiusKey: "OverlayCornerRadius");
        panel.Bind(Visual.IsVisibleProperty, new Binding(nameof(vm.IsStartOpen)));
        var stack = new StackPanel { Spacing = 6 };
        var title = new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, FontSize = 15 };
        ThemeResources.Bind(title, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        stack.Children.Add(title);
        var apps = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<AppEntryViewModel>((app, _) =>
            {
                var button = new Button
                {
                    Content = app.DisplayName, Command = app.LaunchCommand, HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                ThemeResources.Bind(button, Avalonia.Controls.Documents.TextElement.ForegroundProperty, "TextPrimaryBrush");
                return button;
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
            Padding = new Thickness(5),
        };
        ThemeResources.BindSurface(bar, "SurfaceRaisedBrush", borderKey: "BorderDefaultBrush", borderThicknessKey: "ControlBorderThickness");
        var stack = new StackPanel { Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal, Spacing = 4 };
        var launcherButton = new Button
        {
            Content = launcherGlyph, Command = vm.ToggleStartCommand, Width = 38, Height = 34,
        };
        ThemeResources.Bind(launcherButton, Avalonia.Controls.Documents.TextElement.ForegroundProperty, "TextPrimaryBrush");
        ToolTip.SetTip(launcherButton, LocalizedText.Get("shell.launcher.applications", "Applications"));
        stack.Children.Add(launcherButton);
        var groups = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<TaskbarGroupViewModel>((group, _) => TaskbarButton(vm, group)),
        };
        groups.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.TaskbarGroups)));
        stack.Children.Add(groups);
        var showDesktopButton = new Button { Content = "⌄", Command = vm.ShowDesktopCommand, Width = 34, Height = 34 };
        ThemeResources.Bind(showDesktopButton, Avalonia.Controls.Documents.TextElement.ForegroundProperty, "TextPrimaryBrush");
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
        var label = new TextBlock
        {
            Text = name, MaxWidth = 108, MaxLines = 2, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, TextTrimming = DesktopNameTrimming,
            Foreground = ThemeResources.Brush("TextPrimaryBrush"),
        };
        // Desktop icons are narrow, so a long name is shortened to a preview. The untouched name
        // stays reachable from the tooltip rather than being silently lost to the ellipsis.
        ToolTip.SetTip(label, name);
        content.Children.Add(label);
        var button = new Button { Content = content, Width = 116, Height = 84, Margin = new Thickness(3),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Tag = DesktopEntryMarker };
        button.Bind(Button.BackgroundProperty, new Binding("IsDesktopSelected") { Converter = DesktopSelectionBrushConverter.Instance });
        button.Bind(Button.BorderBrushProperty, new Binding("IsDesktopSelected") { Converter = DesktopSelectionBorderBrushConverter.Instance });
        button.Bind(Button.BorderThicknessProperty, new Binding("IsDesktopSelected") { Converter = DesktopSelectionBorderThicknessConverter.Instance });
        button.PointerPressed += (_, args) =>
        {
            var properties = args.GetCurrentPoint(button).Properties;
            if (properties.IsLeftButtonPressed || properties.IsRightButtonPressed)
                vm.SelectDesktopItemCommand.Execute(item);
        };
        button.DoubleTapped += (_, _) =>
        {
            switch (item)
            {
                case AppEntryViewModel app: app.LaunchCommand.Execute(null); break;
                case DesktopFileEntryViewModel file: vm.OpenDesktopEntryCommand.Execute(file); break;
                case ShortcutEntryViewModel shortcut: shortcut.ActivateCommand.Execute(null); break;
            }
        };
        button.ContextMenu = CreateDesktopItemContextMenu(vm, item);
        return button;
    }

    /// <summary>All built-in shells use the same item operations; only their desktop chrome differs.</summary>
    private static ContextMenu CreateDesktopItemContextMenu(DesktopShellViewModel vm, object item)
    {
        if (item is AppEntryViewModel app)
            return new ContextMenu
            {
                ItemsSource = new object[]
                {
                    new MenuItem { Header = LocalizedText.Get("common.open", "Open"), Command = vm.OpenDesktopAppCommand, CommandParameter = app },
                    new Separator(),
                    new MenuItem { Header = LocalizedText.Get("shell.desktop.context.app_details", "App details"), Command = vm.ShowDesktopAppDetailsCommand, CommandParameter = app },
                },
            };

        if (item is ShortcutEntryViewModel shortcut)
            return new ContextMenu
            {
                ItemsSource = new object[]
                {
                    new MenuItem { Header = LocalizedText.Get("common.open", "Open"), Command = shortcut.ActivateCommand },
                },
            };

        if (item is not DesktopFileEntryViewModel file) return new ContextMenu();
        var items = new List<object>
        {
            new MenuItem { Header = LocalizedText.Get("common.open", "Open"), Command = vm.OpenDesktopEntryCommand, CommandParameter = file },
        };
        if (!file.IsDirectory)
            items.Add(new MenuItem { Header = LocalizedText.Get("explorer.open_with", "Open with..."), Command = vm.OpenDesktopEntryWithCommand, CommandParameter = file });
        items.Add(new Separator());
        items.Add(new MenuItem { Header = LocalizedText.Get("common.cut", "Cut"), Command = vm.CutDesktopEntryCommand, CommandParameter = file });
        items.Add(new MenuItem { Header = LocalizedText.Get("common.copy", "Copy"), Command = vm.CopyDesktopEntryCommand, CommandParameter = file });
        if (file.IsDirectory)
            items.Add(new MenuItem { Header = LocalizedText.Get("common.paste", "Paste"), Command = vm.PasteDesktopCommand, CommandParameter = file });
        items.Add(new Separator());
        items.Add(new MenuItem { Header = LocalizedText.Get("common.delete", "Delete"), Command = vm.DeleteDesktopEntryCommand, CommandParameter = file });
        items.Add(new MenuItem { Header = LocalizedText.Get("common.rename", "Rename"), Command = vm.RenameDesktopEntryCommand, CommandParameter = file });
        items.Add(new Separator());
        items.Add(new MenuItem { Header = LocalizedText.Get("shell.desktop.context.show_in_explorer", "Show in File Explorer"), Command = vm.ShowDesktopEntryInExplorerCommand, CommandParameter = file });
        items.Add(new MenuItem { Header = LocalizedText.Get("explorer.properties", "Properties"), Command = vm.ShowDesktopEntryPropertiesCommand, CommandParameter = file });
        return new ContextMenu { ItemsSource = items };
    }

    private void TrackTopLevelActivation()
    {
        var next = TopLevel.GetTopLevel(_root) as Window;
        if (ReferenceEquals(_topLevel, next)) return;
        if (_topLevel is not null) _topLevel.Deactivated -= OnTopLevelDeactivated;
        _topLevel = next;
        if (_topLevel is not null) _topLevel.Deactivated += OnTopLevelDeactivated;
    }

    private void OnTopLevelDeactivated(object? sender, EventArgs args) => _vm?.ClearDesktopSelectionCommand.Execute(null);

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        for (var current = args.Source as Control; current is not null; current = current.Parent as Control)
            if (current is Control { Tag: var tag } && ReferenceEquals(tag, DesktopEntryMarker)) return;
        _vm?.ClearDesktopSelectionCommand.Execute(null);
    }

    private static Button TaskbarButton(DesktopShellViewModel vm, TaskbarGroupViewModel group)
    {
        var glyph = group.IconGlyph ?? "◼";
        var button = new Button
        {
            Command = vm.ToggleTaskbarGroupCommand, CommandParameter = group,
            Width = 38, Height = 34, Padding = new Thickness(2),
        };
        ThemeResources.Bind(button, Avalonia.Controls.Documents.TextElement.ForegroundProperty, "TextPrimaryBrush");
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
        _context.Surfaces.UpdateWorkArea(new RelaxKonOS.Core.Primitives.Rect(0, 0, b.Width, b.Height));
    }
}

internal sealed class DesktopSelectionBrushConverter : IValueConverter
{
    public static readonly DesktopSelectionBrushConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ThemeResources.Brush("DesktopIconSelectedBrush") : Brushes.Transparent;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

internal sealed class DesktopSelectionBorderBrushConverter : IValueConverter
{
    public static readonly DesktopSelectionBorderBrushConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ThemeResources.Brush("DesktopIconSelectedBrush") : Brushes.Transparent;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

internal sealed class DesktopSelectionBorderThicknessConverter : IValueConverter
{
    public static readonly DesktopSelectionBorderThicknessConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? new Thickness(1) : new Thickness(0);
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class WindowsLikeDesktopShell() : LauncherDesktopShellBase(BuiltInShells.Windows)
{
    protected override void BuildLayout(DesktopShellViewModel vm)
    {
        var layout = new WindowsShellLayoutView();
        layout.Compose(Desktop(vm), WindowsTaskbar(vm, Actions), WindowsLauncher(vm));
        _root.Children.Add(layout);
    }

    /// <summary>
    /// The Windows launcher intentionally uses a flat, scrollable application list.  StartApps
    /// is the compatible application catalog, so folders and desktop files never leak into it.
    /// </summary>
    private static Control WindowsLauncher(DesktopShellViewModel vm)
    {
        // The full workspace backdrop dismisses Start without forwarding the click to the
        // desktop beneath it, matching the native Start-menu interaction model.
        var overlay = new Grid { IsVisible = false };
        overlay.Bind(Visual.IsVisibleProperty, new Binding(nameof(vm.IsStartOpen)));
        var dismissArea = new Border { Background = Brushes.Transparent };
        dismissArea.PointerPressed += (_, _) => vm.CloseStartCommand.Execute(null);
        overlay.Children.Add(dismissArea);

        var panel = new Border
        {
            Width = 412,
            MaxHeight = 620,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        ThemeResources.BindSurface(panel, "StartMenuBackgroundBrush", borderKey: "BorderDefaultBrush",
            borderThicknessKey: "ControlBorderThickness", cornerRadiusKey: "OverlayTopCornerRadius");
        ThemeResources.Bind(panel, Border.BoxShadowProperty, "ElevationShadow");
        // Keep clicks inside Start available to its controls; only the transparent surrounding
        // area should dismiss the list.
        panel.PointerPressed += (_, eventArgs) => eventArgs.Handled = true;

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
        overlay.Children.Add(panel);
        return overlay;
    }

    private static Control WindowsSystemRail(DesktopShellViewModel vm)
    {
        var rail = new Border
        {
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
        ThemeResources.Bind(rail, Border.BackgroundProperty, "SurfaceSunkenBrush");
        ThemeResources.Bind(rail, Border.BorderBrushProperty, "BorderSubtleBrush");
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
            FontSize = 13,
        };
        ThemeResources.Bind(name, TextBlock.ForegroundProperty, "TextPrimaryBrush");
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

    private static Control WindowsTaskbar(DesktopShellViewModel vm, IShellActions? actions)
    {
        var bar = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        ThemeResources.Bind(bar, Control.HeightProperty, "TaskbarHeight");
        ThemeResources.Bind(bar, Border.BackgroundProperty, "TaskbarBackgroundBrush");
        ThemeResources.Bind(bar, Border.BorderBrushProperty, "BorderSubtleBrush");
        ThemeResources.Bind(bar, Border.BoxShadowProperty, "WindowShadow");
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var startArea = new StackPanel { Orientation = Orientation.Horizontal };
        var start = WindowsGlyphButton("⊞", LocalizedText.Get("shell.launcher.start", "Start"), vm.ToggleStartCommand);
        // The taskbar's click-to-dismiss handler must not turn an open Start list straight back
        // on when the user clicks its own launcher button.
        start.PointerPressed += (_, eventArgs) => eventArgs.Handled = true;
        startArea.Children.Add(start);

        // Task View sits next to Start, exactly where Windows 11 puts it. The button only asks the
        // host to open the overview: the shell never renders or styles the switcher itself.
        var taskView = WindowsGlyphButton("⧉", LocalizedText.Get("shell.launcher.task_view", "Task view"), null);
        taskView.Click += (_, _) =>
        {
            vm.CloseStartCommand.Execute(null);
            actions?.ShowWindowOverview();
        };
        startArea.Children.Add(taskView);

        layout.Children.Add(startArea);
        layout.PointerPressed += (_, _) => vm.CloseStartCommand.Execute(null);

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
        var time = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
        ThemeResources.Bind(time, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        time.Bind(TextBlock.TextProperty, new Binding(nameof(vm.Clock)));
        var date = new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
        ThemeResources.Bind(date, TextBlock.ForegroundProperty, "TextSecondaryBrush");
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
            HorizontalAlignment = HorizontalAlignment.Center,
            IsVisible = group.IsActive,
        };
        ThemeResources.Bind(activeIndicator, Border.BackgroundProperty, "AccentBrush");
        activeIndicator.Bind(Visual.IsVisibleProperty, new Binding(nameof(group.IsActive)));
        Grid.SetRow(activeIndicator, 1);
        content.Children.Add(activeIndicator);
        var button = new Button
        {
            Content = content,
            Command = vm.ToggleTaskbarGroupCommand,
            CommandParameter = group,
            Padding = new Thickness(2),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
        };
        ThemeResources.Bind(button, Control.WidthProperty, "TaskbarIconSize");
        ThemeResources.Bind(button, Control.HeightProperty, "TaskbarIconSize");
        ToolTip.SetTip(button, group.DisplayName);
        return button;
    }

    private static Button WindowsGlyphButton(string glyph, string tooltip, System.Windows.Input.ICommand? command)
    {
        var glyphText = new TextBlock
        {
            Text = glyph,
            FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ThemeResources.Bind(glyphText, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var button = new Button
        {
            Content = glyphText,
            Command = command,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
        };
        ThemeResources.Bind(button, Control.WidthProperty, "TaskbarIconSize");
        ThemeResources.Bind(button, Control.HeightProperty, "TaskbarIconSize");
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    private static Button WindowsPowerButton(System.Windows.Input.ICommand command)
    {
        // A stroked SVG-style power path avoids font fallback rendering the U+23FB glyph as a box.
        var button = new Button
        {
            Content = ShellIconFactory.Power("TextPrimaryBrush", 20),
            Command = command,
            Width = 48,
            Height = 46,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ThemeResources.Bind(button, Control.WidthProperty, "TaskbarIconSize");
        ThemeResources.Bind(button, Control.HeightProperty, "TaskbarIconSize");
        ToolTip.SetTip(button, LocalizedText.Get("shell.launcher.power", "Power"));
        return button;
    }

    private static Control AppIcon(AppEntryViewModel app, double size)
    {
        if (app.IconImage is { } image)
            return new Image { Source = image, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { Text = app.IconGlyph ?? "◼", FontSize = size - 4, VerticalAlignment = VerticalAlignment.Center };
        ThemeResources.Bind(text, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return text;
    }

    private static Control AppIcon(TaskbarGroupViewModel group, double size)
    {
        if (group.IconImage is { } image)
            return new Image { Source = image, Width = size, Height = size };
        var text = new TextBlock { Text = group.IconGlyph ?? "◼", FontSize = size - 3 };
        ThemeResources.Bind(text, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return text;
    }
}

public sealed class MacosLikeDesktopShell() : LauncherDesktopShellBase(BuiltInShells.Macos)
{
    protected override void BuildLayout(DesktopShellViewModel vm)
    {
        var layout = new MacosShellLayoutView();
        layout.Compose(MacosMenuBar(vm), Desktop(vm), MacosDock(vm), MacosLaunchpad(vm));
        _root.Children.Add(layout);
    }

    /// <summary>macOS-style desktop actions keep view options and background changes prominent.</summary>
    protected override ContextMenu CreateDesktopContextMenu(DesktopShellViewModel vm)
    {
        var menu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                new MenuItem { Header = LocalizedText.Get("shell.desktop.macos.view_options", "Show View Options..."), Command = vm.OpenDesktopDisplaySettingsCommand },
                DesktopIconsToggle(vm),
                new MenuItem { Header = LocalizedText.Get("common.paste", "Paste"), Command = vm.PasteDesktopCommand },
                new Separator(),
                new MenuItem { Header = LocalizedText.Get("shell.desktop.context.open_folder", "Open desktop folder"), Command = vm.OpenDesktopFolderCommand },
                new MenuItem { Header = LocalizedText.Get("shell.desktop.context.open_explorer", "Open File Explorer"), Command = vm.OpenFileExplorerCommand },
                new MenuItem { Header = LocalizedText.Get("shell.desktop.context.open_terminal", "Open Terminal"), Command = vm.OpenTerminalCommand },
                new Separator(),
                new MenuItem { Header = LocalizedText.Get("shell.desktop.macos.change_background", "Change Desktop Background..."), Command = vm.OpenPersonalizationCommand },
            },
        };
        menu.Classes.Add("macos-desktop-context-menu");
        return menu;
    }

    private static Control MacosMenuBar(DesktopShellViewModel vm)
    {
        var bar = new Border
        {
            // TopBarHeight (macOS = 28) is smaller than the 35px menu buttons below; binding it
            // would overflow/clip the buttons. Height stays hardcoded until a menu-button-height
            // token exists to keep them in sync.
            Height = 36,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        ThemeResources.Bind(bar, Border.BackgroundProperty, "SurfaceBrush");
        ThemeResources.Bind(bar, Border.BorderBrushProperty, "BorderSubtleBrush");
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var menus = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        menus.Children.Add(MacosMenuButton("●", LocalizedText.Get("shell.launcher.launchpad", "Launchpad"), vm.ToggleStartCommand, bold: true));
        menus.Children.Add(MacosMenuButton("RelaxKonOS", LocalizedText.Get("shell.launcher.system_settings", "System Settings"), vm.OpenSettingsCommand, bold: true));
        menus.Children.Add(MacosMenuButton(LocalizedText.Get("common.file", "File"), LocalizedText.Get("shell.launcher.open_files", "Open Files"), vm.OpenFileExplorerCommand));
        menus.Children.Add(MacosMenuButton(LocalizedText.Get("common.view", "View"), LocalizedText.Get("shell.launcher.show_desktop", "Show desktop"), vm.ShowDesktopCommand));
        menus.Children.Add(MacosWindowMenuButton(vm));
        menus.Children.Add(MacosMenuButton(LocalizedText.Get("shell.macos.menu.help", "Help"), LocalizedText.Get("shell.macos.menu.help", "Help"), vm.OpenHelpCenterCommand));
        layout.Children.Add(menus);

        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        status.Children.Add(MacosStatusButton("⌂", LocalizedText.Get("shell.launcher.show_desktop", "Show desktop"), vm.ShowDesktopCommand));
        status.Children.Add(MacosStatusButton("⌕", LocalizedText.Get("shell.launcher.open_launchpad", "Open Launchpad"), vm.ToggleStartCommand));
        status.Children.Add(MacosStatusButton("⚙", LocalizedText.Get("shell.launcher.system_settings", "System Settings"), vm.OpenSettingsCommand));
        var clock = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
        ThemeResources.Bind(clock, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        clock.Bind(TextBlock.TextProperty, new Binding(nameof(vm.Clock)));
        var date = new TextBlock { FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        ThemeResources.Bind(date, TextBlock.ForegroundProperty, "TextPrimaryBrush");
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
            Padding = new Thickness(6),
        };
        ThemeResources.BindSurface(dock, "SurfaceRaisedBrush", borderKey: "BorderDefaultBrush",
            borderThicknessKey: "ControlBorderThickness", cornerRadiusKey: "LauncherCornerRadius");
        ThemeResources.Bind(dock, Border.BoxShadowProperty, "ElevationShadow");
        var apps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        apps.Children.Add(MacosDockButton("explorer", LocalizedText.Get("shell.launcher.files", "Files"), vm.OpenFileExplorerCommand));
        apps.Children.Add(MacosDockButton("terminal", LocalizedText.Get("shell.launcher.terminal", "Terminal"), vm.OpenTerminalCommand));
        apps.Children.Add(MacosDockButton("taskmanager", LocalizedText.Get("shell.launcher.task_manager", "Task Manager"), vm.OpenTaskManagerCommand));
        apps.Children.Add(MacosDockButton("settings", LocalizedText.Get("shell.launcher.system_settings", "System Settings"), vm.OpenSettingsCommand));
        var dockDivider1 = new Border { Width = 1, Height = 36, Margin = new Thickness(5, 5) };
        ThemeResources.Bind(dockDivider1, Border.BackgroundProperty, "BorderSubtleBrush");
        apps.Children.Add(dockDivider1);
        var runningApps = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 }),
            ItemTemplate = new FuncDataTemplate<TaskbarGroupViewModel>((group, _) => MacosRunningApp(vm, group)),
        };
        runningApps.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.TaskbarGroups)));
        apps.Children.Add(runningApps);
        var dockDivider2 = new Border { Width = 1, Height = 36, Margin = new Thickness(5, 5) };
        ThemeResources.Bind(dockDivider2, Border.BackgroundProperty, "BorderSubtleBrush");
        apps.Children.Add(dockDivider2);
        apps.Children.Add(MacosDockButton("launchpad", LocalizedText.Get("shell.launcher.launchpad", "Launchpad"), vm.ToggleStartCommand));
        dock.Child = apps;
        return dock;
    }

    private static Control MacosLaunchpad(DesktopShellViewModel vm)
    {
        var overlay = new Border
        {
            IsVisible = false,
        };
        ThemeResources.Bind(overlay, Border.BackgroundProperty, "OverlayScrimBrush");
        overlay.Bind(Visual.IsVisibleProperty, new Binding(nameof(vm.IsStartOpen)));
        overlay.PointerPressed += (_, _) => vm.CloseStartCommand.Execute(null);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 26, Margin = new Thickness(80, 58, 80, 78) };
        var search = new TextBox
        {
            Width = 430,
            Height = 38,
            PlaceholderText = LocalizedText.Get("shell.launcher.search_applications", "Search applications"),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        ThemeResources.Bind(search, TextBox.CornerRadiusProperty, "OverlayCornerRadius");
        search.Bind(TextBox.TextProperty, new Binding(nameof(vm.StartSearchQuery)) { Mode = BindingMode.TwoWay });
        search.PointerPressed += (_, eventArgs) => eventArgs.Handled = true;
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
            FontSize = 12,
            MaxLines = 2,
            MaxWidth = 100,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        ThemeResources.Bind(name, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        Grid.SetRow(name, 1);
        content.Children.Add(name);
        var button = new Button
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
        // Let selecting an app run its existing launch command, rather than treating the app
        // tile itself as a blank Launchpad click.
        button.PointerPressed += (_, eventArgs) => eventArgs.Handled = true;
        return button;
    }

    private static Button MacosRunningApp(DesktopShellViewModel vm, TaskbarGroupViewModel group)
    {
        var icon = MacosIcon(group, 31);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var button = MacosInteractiveButton(icon, vm.ToggleTaskbarGroupCommand, group, group.DisplayName);
        return button;
    }

    /// <summary>Creates a fixed Dock shortcut with the same raster app-icon treatment as running apps.</summary>
    private static Button MacosDockButton(string iconName, string tooltip, System.Windows.Input.ICommand command)
    {
        var image = AppIconImageLoader.Load($"avares://RelaxKonOS.Client/Assets/AppIcons/{iconName}.png");
        return MacosInteractiveButton(new Image
        {
            Source = image,
            Width = 31,
            Height = 31,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        }, command, null, tooltip);
    }

    /// <summary>Creates a static macOS Dock button.</summary>
    private static Button MacosInteractiveButton(Control content, System.Windows.Input.ICommand command, object? parameter, string tooltip)
    {
        var button = new Button
        {
            Content = content,
            Command = command,
            CommandParameter = parameter,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
        };
        ThemeResources.Bind(button, Control.WidthProperty, "TaskbarIconSize");
        ThemeResources.Bind(button, Control.HeightProperty, "TaskbarIconSize");
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    private static Button MacosMenuButton(string title, string tooltip, System.Windows.Input.ICommand command, bool bold = false)
    {
        var button = new Button
        {
            Content = title,
            Command = command,
            Height = 35,
            Padding = new Thickness(8, 0),
            FontSize = 14,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ThemeResources.Bind(button, Avalonia.Controls.Documents.TextElement.ForegroundProperty, "TextPrimaryBrush");
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    /// <summary>Creates the macOS Window menu rather than treating it as a Task Manager shortcut.</summary>
    private static Button MacosWindowMenuButton(DesktopShellViewModel vm)
    {
        var button = MacosMenuButton(
            LocalizedText.Get("shell.macos.menu.window", "Window"),
            LocalizedText.Get("shell.macos.menu.window", "Window"),
            vm.ToggleHostFullScreenCommand);
        button.Command = null;

        var fullScreenItem = new MenuItem { Command = vm.ToggleHostFullScreenCommand };
        var label = new TextBlock();
        label.Bind(TextBlock.TextProperty, new Binding(nameof(vm.HostFullScreenMenuText)) { Source = vm });
        fullScreenItem.Header = label;
        var menu = new ContextMenu { ItemsSource = new object[] { fullScreenItem } };
        button.ContextMenu = menu;
        button.Click += (_, _) => menu.Open(button);
        return button;
    }

    private static Button MacosStatusButton(string glyph, string tooltip, System.Windows.Input.ICommand command)
    {
        var button = new Button
        {
            Content = glyph,
            Command = command,
            Width = 34,
            Height = 35,
            Padding = new Thickness(0),
            FontSize = 20,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ThemeResources.Bind(button, Avalonia.Controls.Documents.TextElement.ForegroundProperty, "TextPrimaryBrush");
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    private static Button MacosPowerButton(System.Windows.Input.ICommand command)
    {
        var button = MacosStatusButton(string.Empty, LocalizedText.Get("shell.launcher.power", "Power"), command);
        button.Content = ShellIconFactory.Power("TextPrimaryBrush", 20);
        return button;
    }

    private static Control MacosIcon(AppEntryViewModel app, double size)
    {
        if (app.IconImage is { } image)
            return new Image { Source = image, Width = size, Height = size };
        var text = new TextBlock { Text = app.IconGlyph ?? "◼", FontSize = size - 6 };
        ThemeResources.Bind(text, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return text;
    }

    private static Control MacosIcon(TaskbarGroupViewModel group, double size)
    {
        if (group.IconImage is { } image)
            return new Image { Source = image, Width = size, Height = size };
        var text = new TextBlock { Text = group.IconGlyph ?? "◼", FontSize = size - 4 };
        ThemeResources.Bind(text, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return text;
    }
}

public sealed class UbuntuLikeDesktopShell() : LauncherDesktopShellBase(BuiltInShells.Ubuntu)
{
    protected override void BuildLayout(DesktopShellViewModel vm)
    {
        var layout = new UbuntuShellLayoutView();
        layout.Compose(UbuntuTopBar(vm), UbuntuDock(vm), Desktop(vm), UbuntuLauncher(vm));
        _root.Children.Add(layout);
    }

    /// <summary>GNOME-style desktop actions prioritize display and background configuration.</summary>
    protected override ContextMenu CreateDesktopContextMenu(DesktopShellViewModel vm)
    {
        var menu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                DesktopIconsToggle(vm),
                new MenuItem { Header = LocalizedText.Get("common.refresh", "Refresh"), Command = vm.RefreshDesktopCommand },
                new MenuItem { Header = LocalizedText.Get("common.paste", "Paste"), Command = vm.PasteDesktopCommand },
                new Separator(),
                new MenuItem { Header = LocalizedText.Get("shell.desktop.ubuntu.display_settings", "Display Settings"), Command = vm.OpenDesktopDisplaySettingsCommand },
                new MenuItem { Header = LocalizedText.Get("shell.desktop.ubuntu.change_background", "Change Background..."), Command = vm.OpenPersonalizationCommand },
                new Separator(),
                new MenuItem { Header = LocalizedText.Get("shell.desktop.context.open_folder", "Open desktop folder"), Command = vm.OpenDesktopFolderCommand },
                new MenuItem { Header = LocalizedText.Get("shell.desktop.context.open_explorer", "Open File Explorer"), Command = vm.OpenFileExplorerCommand },
                new MenuItem { Header = LocalizedText.Get("shell.desktop.context.open_terminal", "Open Terminal"), Command = vm.OpenTerminalCommand },
            },
        };
        menu.Classes.Add("ubuntu-desktop-context-menu");
        return menu;
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
        };
        ThemeResources.Bind(panel, Border.BackgroundProperty, "SurfaceRaisedBrush");
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
            // TopBarHeight (Ubuntu = 32) is smaller than the 38px top-bar buttons below; binding
            // it would overflow/clip them. Height stays hardcoded until a matching button-height
            // token exists.
            Height = 40,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        ThemeResources.Bind(bar, Border.BackgroundProperty, "SurfaceBrush");
        ThemeResources.Bind(bar, Border.BorderBrushProperty, "BorderSubtleBrush");
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
        var topBarTitle = new TextBlock
        {
            Text = "RelaxKonOS",
            Margin = new Thickness(14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
        };
        ThemeResources.Bind(topBarTitle, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        layout.Children.Add(topBarTitle);

        var clock = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var date = new TextBlock { FontSize = 14 };
        ThemeResources.Bind(date, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        date.Bind(TextBlock.TextProperty, new Binding(nameof(vm.DateText)));
        var time = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold };
        ThemeResources.Bind(time, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        time.Bind(TextBlock.TextProperty, new Binding(nameof(vm.Clock)));
        clock.Children.Add(date);
        clock.Children.Add(time);
        Grid.SetColumn(clock, 1);
        layout.Children.Add(clock);

        // Running applications and the available system actions share one horizontal area at
        // the top right, matching a GNOME status region instead of a second vertical dock.
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 3, Margin = new Thickness(0, 0, 9, 0) };
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
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
        ThemeResources.Bind(dock, Border.BackgroundProperty, "SurfaceSunkenBrush");
        ThemeResources.Bind(dock, Border.BorderBrushProperty, "BorderSubtleBrush");
        var actions = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto") };
        actions.Children.Add(UbuntuDockButton("explorer", LocalizedText.Get("shell.launcher.files", "Files"), vm.OpenFileExplorerCommand));
        var terminal = UbuntuDockButton("terminal", LocalizedText.Get("shell.launcher.terminal", "Terminal"), vm.OpenTerminalCommand);
        Grid.SetRow(terminal, 1);
        actions.Children.Add(terminal);
        var taskManager = UbuntuDockButton("taskmanager", LocalizedText.Get("shell.launcher.task_manager", "Task Manager"), vm.OpenTaskManagerCommand);
        Grid.SetRow(taskManager, 2);
        actions.Children.Add(taskManager);
        var settings = UbuntuDockButton("settings", LocalizedText.Get("common.settings", "Settings"), vm.OpenSettingsCommand);
        Grid.SetRow(settings, 3);
        actions.Children.Add(settings);

        var applications = UbuntuDockButton("launchpad", LocalizedText.Get("shell.launcher.show_applications", "Show Applications"), vm.ToggleStartCommand);
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
            FontSize = 12,
            MaxLines = 2,
            MaxWidth = 110,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        ThemeResources.Bind(name, TextBlock.ForegroundProperty, "TextPrimaryBrush");
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
        var icon = UbuntuAppIcon(group, 22);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var button = new Button
        {
            Content = icon,
            Command = vm.ToggleTaskbarGroupCommand,
            CommandParameter = group,
            // Sits in the top bar (not the dock); TaskbarIconSize would overflow the bar and is
            // semantically a dock/taskbar token, so the size stays hardcoded.
            Width = 36,
            Height = 38,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
        };
        ToolTip.SetTip(button, group.DisplayName);
        return button;
    }

    /// <summary>Creates a fixed Ubuntu Dock shortcut using the packaged raster application icon.</summary>
    private static Button UbuntuDockButton(string iconName, string tooltip, System.Windows.Input.ICommand command)
    {
        var image = AppIconImageLoader.Load($"avares://RelaxKonOS.Client/Assets/AppIcons/{iconName}.png");
        var button = new Button
        {
            Content = new Image
            {
                Source = image,
                Width = 34,
                Height = 34,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Command = command,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
        };
        ThemeResources.Bind(button, Control.WidthProperty, "TaskbarIconSize");
        ThemeResources.Bind(button, Control.HeightProperty, "TaskbarIconSize");
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    private static Button UbuntuTopButton(string glyph, string tooltip, System.Windows.Input.ICommand command)
    {
        var glyphText = new TextBlock
        {
            Text = glyph,
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ThemeResources.Bind(glyphText, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var button = new Button
        {
            Content = glyphText,
            Command = command,
            Width = 34,
            Height = 38,
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
        button.Content = ShellIconFactory.Power("TextPrimaryBrush", 18);
        return button;
    }

    private static Control UbuntuAppIcon(AppEntryViewModel app, double size)
    {
        if (app.IconImage is { } image)
            return new Image { Source = image, Width = size, Height = size };
        var text = new TextBlock { Text = app.IconGlyph ?? "◼", FontSize = size - 5 };
        ThemeResources.Bind(text, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return text;
    }

    private static Control UbuntuAppIcon(TaskbarGroupViewModel group, double size)
    {
        if (group.IconImage is { } image)
            return new Image { Source = image, Width = size, Height = size };
        var text = new TextBlock { Text = group.IconGlyph ?? "◼", FontSize = size - 3 };
        ThemeResources.Bind(text, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return text;
    }
}

public static class BuiltInShells
{
    public static readonly ShellDescriptor Windows = new("relaxkonos.windows-like", "Windows-like", "1.0.0", ShellSourceKind.BuiltIn, ShellCapabilities.All);
    public static readonly ShellDescriptor Macos = new("relaxkonos.macos-like", "macOS-like", "1.0.0", ShellSourceKind.BuiltIn, ShellCapabilities.All);
    public static readonly ShellDescriptor Ubuntu = new("relaxkonos.ubuntu-like", "Ubuntu-like", "1.0.0", ShellSourceKind.BuiltIn, ShellCapabilities.All);
    public static readonly IReadOnlyList<ShellDescriptor> All = [Windows, Macos, Ubuntu];
}

/// <summary>Shared vector controls for the built-in shell chrome.</summary>
internal static class ShellIconFactory
{
    public static Viewbox Power(string resourceKey, double size)
    {
        var icon = new VectorPath
        {
            Data = StreamGeometry.Parse("M 12,2 L 12,11 M 7.05,5.05 A 7,7 0 1 0 16.95,5.05"),
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
        };
        icon.Bind(VectorPath.StrokeProperty, icon.GetResourceObservable(resourceKey));
        return new Viewbox { Width = size, Height = size, Child = icon };
    }
}
