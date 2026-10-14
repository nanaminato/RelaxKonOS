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

    private async void Management_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section } && DataContext is ServerCenterViewModel { IsBusy: false } viewModel
            && viewModel.ShowManagementDialogAsync is { } show)
        {
            if (section is "settings" or "components" or "repair")
            {
                await viewModel.ProbeHostCommand.ExecuteAsync(null);
                if (section is "settings" or "components" && !viewModel.PrepareConfiguration()) return;
            }
            await show(section);
        }
    }
}
