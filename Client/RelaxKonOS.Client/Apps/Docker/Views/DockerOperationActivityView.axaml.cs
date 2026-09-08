using Avalonia.Controls;

namespace RelaxKonOS.Client.Apps.Docker;

/// <summary>Shared, read-only progress and bounded diagnostic output for a Docker command.</summary>
internal partial class DockerOperationActivityView : UserControl
{
    public DockerOperationActivityView() => InitializeComponent();
}
