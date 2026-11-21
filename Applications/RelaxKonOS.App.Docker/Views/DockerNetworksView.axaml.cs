using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.App.Docker.Views;

public partial class DockerNetworksView : UserControl
{
    private readonly Func<Task> _showCreateDialog;

    public DockerNetworksView(Func<Task> showCreateDialog)
    {
        _showCreateDialog = showCreateDialog;
        InitializeComponent();
    }

    private async void CreateNetwork_Click(object? sender, RoutedEventArgs e) => await _showCreateDialog();
}
