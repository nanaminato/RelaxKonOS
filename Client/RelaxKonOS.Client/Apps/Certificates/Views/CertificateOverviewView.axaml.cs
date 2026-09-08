using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.Certificates.Views;

internal partial class CertificateOverviewView : UserControl
{
    private readonly Func<Task> _showRequestCertificate;
    private readonly Func<Task> _showCreateSelfSignedCertificate;

    public CertificateOverviewView(Func<Task> showRequestCertificate, Func<Task> showCreateSelfSignedCertificate)
    {
        _showRequestCertificate = showRequestCertificate;
        _showCreateSelfSignedCertificate = showCreateSelfSignedCertificate;
        InitializeComponent();
    }

    private async void RequestCertificate_Click(object? sender, RoutedEventArgs e) => await _showRequestCertificate();
    private async void CreateSelfSignedCertificate_Click(object? sender, RoutedEventArgs e) => await _showCreateSelfSignedCertificate();
}
