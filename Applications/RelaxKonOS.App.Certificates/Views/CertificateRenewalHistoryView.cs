using RelaxKonOS.App.Certificates;
using Avalonia.Controls;
using Avalonia.Data;

namespace RelaxKonOS.App.Certificates.Views;

public sealed class CertificateRenewalHistoryView : UserControl
{
    private readonly TextBlock _records = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    public CertificateRenewalHistoryView(CertificateManagerViewModel vm, Guid certificateId)
    {
        var refresh = new Button { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        refresh.Bind(ContentControl.ContentProperty, new Binding(nameof(CertificateManagerViewModel.CurrentLanguage))
        {
            Source = vm,
            Converter = LocExtension.Converter,
            ConverterParameter = "certificates.refresh",
        });
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Avalonia.Thickness(20), RowSpacing = 12 };
        grid.Children.Add(refresh);
        var scroll = new ScrollViewer { Content = _records };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        Content = grid;
        async Task LoadAsync()
        {
            refresh.IsEnabled = false;
            _records.Text = CertificateLocalization.Resolve(vm.CurrentLanguage, "certificates.status.loading");
            try { _records.Text = await vm.LoadRenewalHistoryAsync(certificateId); }
            catch (Exception) { _records.Text = CertificateLocalization.Resolve(vm.CurrentLanguage, "certificates.error.request_failed"); }
            finally { refresh.IsEnabled = true; }
        }
        refresh.Click += async (_, _) => await LoadAsync();
        _ = LoadAsync();
    }
}
