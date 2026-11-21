using RelaxKonOS.App.ImageViewer;
using RelaxKonOS.Core.Applications;

namespace RelaxKonOS.Examples.ImageViewerPilot;

/// <summary>Third-party example of the SDK file-opening and window contracts.</summary>
public sealed class ImageViewerPilotApp : ImageViewerApp
{
    public override ApplicationManifest Manifest { get; }

    public ImageViewerPilotApp()
    {
        Manifest = base.Manifest with { Id = new AppId("com.relaxkonos.example.image-viewer"), DisplayName = "Image Viewer Pilot", Version = "0.1.0-dev" };
    }

    protected override string? TitleOverride(string? path) =>
        string.IsNullOrWhiteSpace(path) ? Manifest.DisplayName : $"{Path.GetFileName(path)} · {Manifest.DisplayName}";
}
