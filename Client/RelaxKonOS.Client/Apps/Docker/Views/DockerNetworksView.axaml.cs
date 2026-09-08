using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.Docker.Views;

internal partial class DockerNetworksView : UserControl
{
    private readonly Func<Task> _showCreateDialog;

    public DockerNetworksView(Func<Task> showCreateDialog)
    {
        _showCreateDialog = showCreateDialog;
        InitializeComponent();
    }

    private async void CreateNetwork_Click(object? sender, RoutedEventArgs e) => await _showCreateDialog();
}
