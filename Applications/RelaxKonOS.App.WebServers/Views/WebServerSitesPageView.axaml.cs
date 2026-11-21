using RelaxKonOS.App.WebServers;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.App.WebServers.Views;

public partial class WebServerSitesPageView : UserControl
{
    public WebServerSitesPageView() => InitializeComponent();

    private async void ServiceAddress_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string path }
            && DataContext is WebServerManagerViewModel viewModel)
            await viewModel.OpenSiteDirectoryAsync(path);
    }
}
