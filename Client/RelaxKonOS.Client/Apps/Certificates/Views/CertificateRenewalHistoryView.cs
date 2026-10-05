using Avalonia.Controls;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.Certificates.Views;

internal sealed class CertificateRenewalHistoryView : UserControl
{
    private readonly TextBlock _records = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    public CertificateRenewalHistoryView(CertificateManagerViewModel vm, Guid certificateId)
    {
        var refresh = new Button { Content = LocalizedText.Get("common.refresh"), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Avalonia.Thickness(20), RowSpacing = 12 };
        grid.Children.Add(refresh);
        var scroll = new ScrollViewer { Content = _records };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        Content = grid;
        async Task LoadAsync()
        {
            refresh.IsEnabled = false;
            _records.Text = LocalizedText.Get("certificates.status.loading");
            try { _records.Text = await vm.LoadRenewalHistoryAsync(certificateId); }
            catch (Exception) { _records.Text = LocalizedText.Get("certificates.error.request_failed"); }
            finally { refresh.IsEnabled = true; }
        }
        refresh.Click += async (_, _) => await LoadAsync();
        _ = LoadAsync();
    }
}
