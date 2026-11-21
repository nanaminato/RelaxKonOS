using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using RelaxKonOS.Protocol.Certificates;

namespace RelaxKonOS.App.Certificates.Views;

public partial class CertificateListView : UserControl
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
        foreach (var column in CertificatesGrid.Columns.OfType<DataGridTextColumn>()
            .Where(column => column.SortMemberPath is nameof(CertificateDto.Kind) or nameof(CertificateDto.Status) or nameof(CertificateDto.ChallengeType)))
        {
            column.Binding = new MultiBinding
            {
                Converter = CertificateEnumTextConverter.Instance,
                Bindings =
                {
                    new Binding(column.SortMemberPath),
                    new Binding($"DataContext.{nameof(CertificateManagerViewModel.CurrentLanguage)}") { Source = this },
                },
            };
        }
    }

    private void RenewalHistory_Click(object? sender, RoutedEventArgs e) => _showRenewalHistory();
    private async void RequestCertificate_Click(object? sender, RoutedEventArgs e) => await _showRequestCertificate();
    private async void CreateSelfSignedCertificate_Click(object? sender, RoutedEventArgs e) => await _showCreateSelfSignedCertificate();
}
