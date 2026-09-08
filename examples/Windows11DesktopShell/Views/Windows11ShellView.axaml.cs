using Avalonia.Controls;
using Avalonia.Input;
using Example.Windows11DesktopShell.ViewModels;

namespace Example.Windows11DesktopShell.Views;

public partial class Windows11ShellView : UserControl
{
    public Windows11ShellView() => InitializeComponent();

    public Canvas WindowHostSurface => WindowHost;
    public Canvas FullScreenHostSurface => FullScreenHost;
    public Panel ShellOverlaySurface => ShellOverlayHost;
    public Control InputBackdropSurface => InputBackdrop;

    private void Desktop_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        for (var current = e.Source as Control; current is not null; current = current.Parent as Control)
            if (current is DesktopShortcutsView) return;
        if (DataContext is Windows11ShellViewModel viewModel) viewModel.ClearDesktopSelection();
    }
}
