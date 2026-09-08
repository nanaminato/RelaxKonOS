using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.Docker.Views;

internal partial class DockerStacksView : UserControl
{
    private readonly Func<Task> _showDeployDialog;

    public DockerStacksView(Func<Task> showDeployDialog)
    {
        _showDeployDialog = showDeployDialog;
        InitializeComponent();
    }

    private async void DeployStack_Click(object? sender, RoutedEventArgs e) => await _showDeployDialog();
}
