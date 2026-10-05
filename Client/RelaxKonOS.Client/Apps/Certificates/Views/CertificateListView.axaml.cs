using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.Certificates.Views;

internal partial class CertificateListView : UserControl
{
    private readonly Action _showRenewalHistory;
    private readonly Func<Task> _showRequestCertificate;
    private readonly Func<Task> _showCreateSelfSignedCertificate;

    public CertificateListView(Func<Task> showRequestCertificate, Func<Task> showCreateSelfSignedCertificate, Action showRenewalHistory)
    {
        _showRequestCertificate = showRequestCertificate;
        _showCreateSelfSignedCertificate = showCreateSelfSignedCertificate;
        _showRenewalHistory = showRenewalHistory;
        InitializeComponent();
    }

    private void RenewalHistory_Click(object? sender, RoutedEventArgs e) => _showRenewalHistory();
    private async void RequestCertificate_Click(object? sender, RoutedEventArgs e) => await _showRequestCertificate();
    private async void CreateSelfSignedCertificate_Click(object? sender, RoutedEventArgs e) => await _showCreateSelfSignedCertificate();
}
