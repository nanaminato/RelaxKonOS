using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.WebServers.Views;

internal partial class WebServerSitesPageView : UserControl
{
    public WebServerSitesPageView() => InitializeComponent();

    private async void ServiceAddress_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string path }
            && DataContext is WebServerManagerViewModel viewModel)
            await viewModel.OpenSiteDirectoryAsync(path);
    }
}
