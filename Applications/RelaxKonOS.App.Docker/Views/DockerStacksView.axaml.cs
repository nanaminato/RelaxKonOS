using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.App.Docker.Views;

public partial class DockerStacksView : UserControl
{
    private readonly Func<Task> _showDeployDialog;

    public DockerStacksView(Func<Task> showDeployDialog)
    {
        _showDeployDialog = showDeployDialog;
        InitializeComponent();
    }

    private async void DeployStack_Click(object? sender, RoutedEventArgs e) => await _showDeployDialog();
}
