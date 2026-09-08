using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.Certificates.Views;

internal partial class SelfSignedCertificateDialogView : UserControl
{
    private readonly CertificateManagerViewModel _viewModel;
    private readonly ModalDialog<bool> _dialog;

    public SelfSignedCertificateDialogView(CertificateManagerViewModel viewModel, ModalDialog<bool> dialog)
    {
        _viewModel = viewModel;
        _dialog = dialog;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _dialog.Cancel();

    private async void Create_Click(object? sender, RoutedEventArgs e)
    {
        if (await _viewModel.TryCreateSelfSignedCertificateAsync()) _dialog.Close(true);
    }
}
