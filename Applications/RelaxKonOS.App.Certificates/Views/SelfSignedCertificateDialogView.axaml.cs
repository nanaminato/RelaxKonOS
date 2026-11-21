using RelaxKonOS.App.Certificates;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.App.Certificates.Views;

public partial class SelfSignedCertificateDialogView : UserControl
{
    private readonly CertificateManagerViewModel _viewModel;
    private readonly ModalDialog<bool> _dialog;

    public SelfSignedCertificateDialogView(CertificateManagerViewModel viewModel, ModalDialog<bool> dialog,
        Action<ModalDialog<bool>, Func<bool>, Func<bool>> attachDraftGuard)
    {
        _viewModel = viewModel;
        _dialog = dialog;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.EditorStatus = default;
        var initial = (viewModel.SelfSignedDomains, viewModel.SelfSignedValidityDays, viewModel.SelectedKeyAlgorithm?.Value);
        attachDraftGuard(dialog, () => !viewModel.CanEditCertificateDraft,
            () => initial != (viewModel.SelfSignedDomains, viewModel.SelfSignedValidityDays, viewModel.SelectedKeyAlgorithm?.Value));
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _dialog.Cancel();

    private async void Create_Click(object? sender, RoutedEventArgs e)
    {
        if (await _viewModel.TryCreateSelfSignedCertificateAsync()) _dialog.Close(true);
    }
}
