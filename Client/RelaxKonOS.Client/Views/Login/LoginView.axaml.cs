using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.Client.ViewModels.Login;
using System.ComponentModel;
using RelaxKonOS.Client.Services.Auth;

namespace RelaxKonOS.Client.Views.Login;

public partial class LoginView : UserControl
{
    private LoginViewModel? _viewModel;
    private bool _hostKeyDialogOpen;
    private bool _certificateDialogOpen;

    public LoginView()
    {
        InitializeComponent();
        DataContextChanged += LoginView_DataContextChanged;
    }

    private void LoginView_DataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= LoginViewModel_PropertyChanged;
            _viewModel.ConfirmServerCertificateAsync = null;
        }
        _viewModel = DataContext as LoginViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += LoginViewModel_PropertyChanged;
            var viewModel = _viewModel;
            viewModel.ConfirmServerCertificateAsync = review => ShowCertificateDialogAsync(viewModel, review);
        }
    }

    private async Task<bool> ShowCertificateDialogAsync(LoginViewModel viewModel, ServerCertificateReview review)
    {
        if (_certificateDialogOpen || TopLevel.GetTopLevel(this) is not Window owner) return false;
        _certificateDialogOpen = true;
        try
        {
            return await new SshHostKeyDialog(viewModel.CertificateDialogTitle, viewModel.CertificateReviewText(review),
                viewModel.CertificateFingerprintLabel, review.Fingerprint, viewModel.PreviousCertificateFingerprintLabel,
                review.PreviousFingerprint ?? string.Empty, string.Empty, viewModel.TrustCertificateText,
                viewModel.CancelText).ShowDialog<bool>(owner);
        }
        finally { _certificateDialogOpen = false; }
    }

    private void LoginViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LoginViewModel.NeedsHostKeyConfirmation) &&
            _viewModel is { NeedsHostKeyConfirmation: true })
            _ = ShowHostKeyDialogAsync(_viewModel);
    }

    private async Task ShowHostKeyDialogAsync(LoginViewModel viewModel)
    {
        if (_hostKeyDialogOpen || TopLevel.GetTopLevel(this) is not Window owner) return;
        _hostKeyDialogOpen = true;
        try
        {
            var accepted = await new SshHostKeyDialog(
                viewModel.HostKeyDialogTitle, viewModel.HostKeyMessage,
                viewModel.ObservedFingerprintLabel, viewModel.HostKeyFingerprint,
                viewModel.PinnedFingerprintLabel, viewModel.PreviousHostKeyFingerprint,
                viewModel.PreviousHostKeyConfirmedText,
                viewModel.ConfirmHostKeyText, viewModel.CancelText).ShowDialog<bool>(owner);
            if (accepted) await viewModel.ConfirmHostKeyFromDialogAsync();
            else viewModel.CancelHostKeyConfirmation();
        }
        finally
        {
            _hostKeyDialogOpen = false;
            if (viewModel.NeedsHostKeyConfirmation)
                _ = ShowHostKeyDialogAsync(viewModel);
        }
    }

    private async void ServerAddress_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel viewModel && !viewModel.IsConnecting)
            await viewModel.DiscoverServerEndpointAsync();
    }

    private void RelaxLogin_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel login) login.UseSshLogin = false;
    }

    private void SshLogin_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel login) login.UseSshLogin = true;
    }
}
