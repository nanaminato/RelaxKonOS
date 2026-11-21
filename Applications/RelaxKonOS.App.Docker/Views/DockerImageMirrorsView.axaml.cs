using Avalonia.Controls;

namespace RelaxKonOS.App.Docker.Views;

public partial class DockerImageMirrorsView : UserControl
{
    public DockerImageMirrorsView() => InitializeComponent();

    public DockerImageMirrorsView(DockerImageMirrorsViewModel viewModel) : this() => DataContext = viewModel;
}
