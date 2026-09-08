using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.Docker.Views;

internal partial class DockerNetworkDialogView : UserControl
{
    private readonly DockerManagerViewModel _viewModel;
    private readonly ModalDialog<bool> _dialog;

    public DockerNetworkDialogView(DockerManagerViewModel viewModel, ModalDialog<bool> dialog)
    {
        _viewModel = viewModel;
        _dialog = dialog;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _dialog.Cancel();
    private async void Create_Click(object? sender, RoutedEventArgs e) { if (await _viewModel.TryCreateNetworkAsync()) _dialog.Close(true); }
}
