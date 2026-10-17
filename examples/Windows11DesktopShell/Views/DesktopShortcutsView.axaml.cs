using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RelaxKonOS.Example.Windows11DesktopShell.ViewModels;
using RelaxKonOS.Shell;

namespace RelaxKonOS.Example.Windows11DesktopShell.Views;

public partial class DesktopShortcutsView : UserControl
{
    public DesktopShortcutsView() => InitializeComponent();

    private void DesktopShortcut_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        if (!(properties.IsLeftButtonPressed || properties.IsRightButtonPressed)
            || sender is not Control { DataContext: ShellDesktopEntry entry }
            || DataContext is not Windows11ShellViewModel viewModel) return;
        if (properties.IsRightButtonPressed && entry.IsSelected) return;
        var selected = viewModel.DesktopEntries.Where(item => item.IsSelected).Select(item => item.Id).ToArray();
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 && properties.IsLeftButtonPressed)
            viewModel.SetDesktopSelection(entry.IsSelected ? selected.Except([entry.Id]) : selected.Append(entry.Id));
        else if ((e.KeyModifiers & KeyModifiers.Shift) != 0 && properties.IsLeftButtonPressed)
            viewModel.SetDesktopSelection(selected.Append(entry.Id));
        else viewModel.SelectDesktopEntry(entry);
    }

    private void DesktopShortcut_OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control { DataContext: ShellDesktopEntry entry } control
            && DataContext is Windows11ShellViewModel viewModel)
        {
            control.ContextMenu = CreateContextMenu(viewModel, entry);
            control.RemoveHandler(PointerPressedEvent, DesktopShortcut_OnPointerPressed);
            control.AddHandler(PointerPressedEvent, DesktopShortcut_OnPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        }
    }

    private async void DesktopShortcut_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: ShellDesktopEntry entry }
            || DataContext is not Windows11ShellViewModel viewModel) return;
        await viewModel.OpenDesktopEntryAsync(entry);
    }

    private async void DesktopShortcut_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not Control { DataContext: ShellDesktopEntry entry }
            || DataContext is not Windows11ShellViewModel vm) return;
        e.Handled = true;
        await vm.OpenDesktopEntryAsync(entry);
    }

    private static ContextMenu CreateContextMenu(Windows11ShellViewModel viewModel, ShellDesktopEntry entry)
    {
        var items = new List<object>
        {
            new MenuItem { Header = viewModel.Open, Command = viewModel.OpenDesktopEntryCommand, CommandParameter = entry },
        };
        if (entry.Kind is ShellDesktopEntryKind.File)
            items.Add(new MenuItem { Header = viewModel.OpenWith, Command = viewModel.OpenDesktopEntryWithCommand, CommandParameter = entry });
        if (entry.Kind is ShellDesktopEntryKind.File or ShellDesktopEntryKind.Folder)
        {
            items.Add(new Separator());
            items.Add(new MenuItem { Header = viewModel.Copy, Command = viewModel.CopyDesktopEntryCommand, CommandParameter = entry });
            items.Add(new MenuItem { Header = viewModel.Cut, Command = viewModel.CutDesktopEntryCommand, CommandParameter = entry });
            if (entry.Kind == ShellDesktopEntryKind.Folder)
                items.Add(new MenuItem { Header = viewModel.Paste, Command = viewModel.PasteDesktopEntryCommand, CommandParameter = entry });
            items.Add(new Separator());
            items.Add(new MenuItem { Header = viewModel.ShowInExplorer, Command = viewModel.ShowDesktopEntryInExplorerCommand, CommandParameter = entry });
            items.Add(new MenuItem { Header = viewModel.Properties, Command = viewModel.ShowDesktopEntryPropertiesCommand, CommandParameter = entry });
            items.Add(new Separator());
            items.Add(new MenuItem { Header = viewModel.Delete, Command = viewModel.DeleteDesktopEntryCommand, CommandParameter = entry });
        }
        else if (entry.Kind == ShellDesktopEntryKind.Application)
        {
            items.Add(new Separator());
            items.Add(new MenuItem { Header = viewModel.AppDetails, Command = viewModel.ShowDesktopEntryPropertiesCommand, CommandParameter = entry });
        }
        return new ContextMenu { ItemsSource = items };
    }
}
