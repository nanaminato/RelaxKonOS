using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.Docker.Views;

internal partial class DockerImagesView : UserControl
{
    private readonly Func<Task> _showPullDialog;

    public DockerImagesView(Func<Task> showPullDialog)
    {
        _showPullDialog = showPullDialog;
        InitializeComponent();
    }

    private async void PullImage_Click(object? sender, RoutedEventArgs e) => await _showPullDialog();
}
