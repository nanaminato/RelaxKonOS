using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.ServerCenter.Views;

public partial class ServerCenterDeploymentPage : UserControl
{
    private readonly IServerCenterDeploymentPresentation _presentation;

    public ServerCenterDeploymentPage(IServerCenterDeploymentPresentation presentation)
    {
        _presentation = presentation;
        InitializeComponent();
        DataContext = presentation;
        AttachedToVisualTree += async (_, _) => await _presentation.EnsureSelectedHostPlatformAsync();
    }

    private async void SelectedHost_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        await _presentation.EnsureSelectedHostPlatformAsync();

    private async void Management_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section })
            await _presentation.ShowManagementSectionAsync(section);
    }
}
