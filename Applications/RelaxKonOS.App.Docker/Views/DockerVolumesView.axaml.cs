using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.App.Docker.Views;

public partial class DockerVolumesView : UserControl
{
    private readonly Func<Task> _showCreateDialog;

    public DockerVolumesView(Func<Task> showCreateDialog)
    {
        _showCreateDialog = showCreateDialog;
        InitializeComponent();
    }

    private async void CreateVolume_Click(object? sender, RoutedEventArgs e) => await _showCreateDialog();
}
