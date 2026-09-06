using Avalonia.Controls;
using Avalonia.Input;
using Example.Windows11DesktopShell.ViewModels;
using RemoteOS.Shell;

namespace Example.Windows11DesktopShell.Views;

public partial class DesktopShortcutsView : UserControl
{
    public DesktopShortcutsView() => InitializeComponent();

    private void DesktopShortcut_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || sender is not Control { DataContext: ShellDesktopEntry entry }
            || DataContext is not Windows11ShellViewModel viewModel) return;
        viewModel.SelectDesktopEntry(entry);
    }

    private async void DesktopShortcut_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: ShellDesktopEntry entry }
            || DataContext is not Windows11ShellViewModel viewModel) return;
        await viewModel.OpenDesktopEntryAsync(entry);
    }
}
