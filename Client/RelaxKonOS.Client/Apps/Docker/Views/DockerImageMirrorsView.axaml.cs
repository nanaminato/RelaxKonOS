using Avalonia.Controls;

namespace RelaxKonOS.Client.Apps.Docker.Views;

public partial class DockerImageMirrorsView : UserControl
{
    public DockerImageMirrorsView(DockerImageMirrorsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
