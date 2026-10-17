using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RelaxKonOS.Shell;
using Avalonia.Input;
using RelaxKonOS.Example.Windows11DesktopShell.ViewModels;

namespace RelaxKonOS.Example.Windows11DesktopShell.Views;

public partial class Windows11ShellView : UserControl
{
    public Windows11ShellView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => TaskbarApplicationScroller.MaxWidth = Math.Max(80, Bounds.Width - 440);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is Windows11ShellViewModel vm && (vm.IsStartOpen || vm.IsQuickSettingsOpen))
            {
                vm.CloseFlyoutsCommand.Execute(null);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        DesktopArea.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(DesktopArea).Properties.IsLeftButtonPressed) return;
            if (IsWindowInput(e.Source)) return;
            for (var control = e.Source as Control; control is not null && control != DesktopArea; control = control.Parent as Control)
                if (control is Button || control == WindowHost) return;
            if (DataContext is not Windows11ShellViewModel vm) return;
            _initialSelection = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Shift)) != 0
                ? vm.DesktopEntries.Where(item => item.IsSelected).Select(item => item.Id).ToArray() : [];
            vm.SetDesktopSelection(_initialSelection);
            _origin = e.GetPosition(DesktopArea);
            DesktopArea.Focus();
            _selectionPointer = e.Pointer;
            e.Pointer.Capture(DesktopArea);
            e.Handled = true;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        DesktopArea.PointerMoved += (_, e) =>
        {
            if (_origin is not { } origin || DataContext is not Windows11ShellViewModel vm) return;
            var bounds = new Rect(origin, e.GetPosition(DesktopArea)).Normalize();
            if (bounds.Width < 4 && bounds.Height < 4) return;
            SelectionRectangle.IsVisible = true;
            Canvas.SetLeft(SelectionRectangle, bounds.X);
            Canvas.SetTop(SelectionRectangle, bounds.Y);
            SelectionRectangle.Width = bounds.Width;
            SelectionRectangle.Height = bounds.Height;
            var hits = DesktopArea.GetVisualDescendants().OfType<Button>()
                .Where(button => button.IsEffectivelyVisible && button.DataContext is ShellDesktopEntry)
                .Where(button => button.TranslatePoint(default, DesktopArea) is { } point && bounds.Intersects(new Rect(point, button.Bounds.Size)))
                .Select(button => ((ShellDesktopEntry)button.DataContext!).Id);
            vm.SetDesktopSelection(_initialSelection.Concat(hits));
            e.Handled = true;
        };
        DesktopArea.PointerReleased += (_, e) =>
        {
            if (_origin is null) return;
            EndSelection();
            e.Handled = true;
        };
        DesktopArea.PointerCaptureLost += (_, _) => EndSelection();
        DesktopArea.KeyDown += (_, e) =>
        {
            if (IsWindowInput(e.Source) || e.Handled) return;
            if (DataContext is not Windows11ShellViewModel vm) return;
            if (e.Key == Key.A && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
            {
                if (vm.AreDesktopIconsVisible) vm.SetDesktopSelection(vm.DesktopEntries.Select(item => item.Id));
                e.Handled = true;
            }
            else if (e.Key is Key.C or Key.X && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
            {
                var entry = vm.DesktopEntries.FirstOrDefault(item => item.IsSelected && item.Kind is ShellDesktopEntryKind.File or ShellDesktopEntryKind.Folder);
                if (entry is not null) (e.Key == Key.C ? vm.CopyDesktopEntryCommand : vm.CutDesktopEntryCommand).Execute(entry);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                var entry = vm.DesktopEntries.FirstOrDefault(item => item.IsSelected);
                if (entry is not null) vm.DeleteDesktopEntryCommand.Execute(entry);
                e.Handled = true;
            }
            else if (e.Key == Key.V && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
            {
                vm.PasteDesktopCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                EndSelection();
                vm.ClearDesktopSelection();
                e.Handled = true;
            }
        };
    }

    private void AllApplication_OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Control { DataContext: ShellApplicationEntry entry } control || DataContext is not Windows11ShellViewModel vm) return;
        var menu = new ContextMenu();
        menu.Opening += (_, _) => menu.ItemsSource = new object[]
        {
            new MenuItem { Header = vm.TaskbarEntries.Any(item => item.Id == entry.Id && item.IsPinned) ? vm.UnpinFromTaskbar : vm.PinToTaskbar, Command = vm.ToggleApplicationPinCommand, CommandParameter = entry },
        };
        control.ContextMenu = menu;
    }

    private void TaskbarApplication_OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Control { DataContext: ShellTaskbarEntry entry } control || DataContext is not Windows11ShellViewModel vm) return;
        control.ContextMenu = new ContextMenu { ItemsSource = new object[]
        {
            new MenuItem { Header = entry.IsPinned ? vm.UnpinFromTaskbar : vm.PinToTaskbar, Command = vm.ToggleTaskbarPinCommand, CommandParameter = entry },
        } };
    }

    private bool IsWindowInput(object? source) => source is Visual visual &&
        (ReferenceEquals(visual, WindowHost) || visual.GetVisualAncestors().Contains(WindowHost));

    private void EndSelection()
    {
        _origin = null;
        SelectionRectangle.IsVisible = false;
        var captured = _selectionPointer;
        _selectionPointer = null;
        captured?.Capture(null);
    }

    private IPointer? _selectionPointer;
    private Point? _origin;
    private string[] _initialSelection = [];

    public Canvas WindowHostSurface => WindowHost;
    public Canvas FullScreenHostSurface => FullScreenHost;
    public Panel ShellOverlaySurface => ShellOverlayHost;
    public Control InputBackdropSurface => InputBackdrop;

    private void Desktop_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled) return;
        for (var current = e.Source as Control; current is not null; current = current.Parent as Control)
            if (current is DesktopShortcutsView) return;
        if (DataContext is Windows11ShellViewModel viewModel) viewModel.ClearDesktopSelection();
    }
}
