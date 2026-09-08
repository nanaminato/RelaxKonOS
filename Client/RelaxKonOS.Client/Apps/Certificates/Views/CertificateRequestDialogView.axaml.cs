using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.Certificates.Views;

internal partial class CertificateRequestDialogView : UserControl
{
    private readonly CertificateManagerViewModel _viewModel;
    private readonly ModalDialog<bool> _dialog;

    public CertificateRequestDialogView(CertificateManagerViewModel viewModel, ModalDialog<bool> dialog)
    {
        _viewModel = viewModel;
        _dialog = dialog;
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.IsOperationRunning)
            await _viewModel.CancelActiveOperationAsync();
        _dialog.Cancel();
    }

    private async void Request_Click(object? sender, RoutedEventArgs e)
    {
        if (await _viewModel.TryRequestCertificateAsync())
            _dialog.Close(true);
    }
}
