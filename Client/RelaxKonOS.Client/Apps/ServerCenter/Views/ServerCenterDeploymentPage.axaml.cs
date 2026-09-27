using Avalonia.Controls;
using RelaxKonOS.Client.ViewModels.ServerCenter;
namespace RelaxKonOS.Client.Apps.ServerCenter.Views;
public partial class ServerCenterDeploymentPage : UserControl
{
    public ServerCenterDeploymentPage()
    {
        InitializeComponent();
        AttachedToVisualTree += async (_, _) =>
        {
            if (DataContext is ServerCenterViewModel viewModel)
                await viewModel.EnsureSelectedHostPlatformAsync();
        };
    }

    private async void SelectedHost_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ServerCenterViewModel viewModel)
            await viewModel.EnsureSelectedHostPlatformAsync();
    }
}
