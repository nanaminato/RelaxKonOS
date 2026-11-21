using Avalonia.Controls;

namespace RelaxKonOS.App.Docker.Views;

public partial class DockerProxyView : UserControl
{
    public DockerProxyView() => InitializeComponent();

    public DockerProxyView(DockerProxyStatusViewModel viewModel) : this() => DataContext = viewModel;
}
