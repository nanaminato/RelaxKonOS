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
        viewModel.EditorStatus = string.Empty;
        var initial = (viewModel.Domains, viewModel.ContactEmail, viewModel.SelectedChallengeType?.Value,
            viewModel.SelectedKeyAlgorithm?.Value, viewModel.AcceptedTerms, viewModel.PublicReachabilityConfirmed);
        RelaxKonOS.Client.Services.Dialogs.DraftDialogGuard.Attach(dialog, () => !viewModel.CanEditCertificateDraft,
            () => initial != (viewModel.Domains, viewModel.ContactEmail, viewModel.SelectedChallengeType?.Value,
                viewModel.SelectedKeyAlgorithm?.Value, viewModel.AcceptedTerms, viewModel.PublicReachabilityConfirmed));
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _dialog.Cancel();

    private async void Request_Click(object? sender, RoutedEventArgs e)
    {
        if (await _viewModel.TryRequestCertificateAsync())
            _dialog.Close(true);
    }
}
