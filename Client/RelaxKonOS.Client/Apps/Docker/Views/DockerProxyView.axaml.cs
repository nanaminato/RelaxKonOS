using Avalonia.Controls;

namespace RelaxKonOS.Client.Apps.Docker.Views;

/// <summary>Network proxy page. All state and commands live in the view model.</summary>
internal partial class DockerProxyView : UserControl
{
    public DockerProxyView(DockerProxyViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
