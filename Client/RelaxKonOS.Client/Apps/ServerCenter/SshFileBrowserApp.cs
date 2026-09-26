using RelaxKonOS.Client.Localization;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.ServerCenter.Views;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.Apps.CodeEditor;
using RelaxKonOS.Client.Apps.ImageViewer;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using AppContext = RelaxKonOS.AppSDK.AppContext;
using Rect = RelaxKonOS.Core.Primitives.Rect;

namespace RelaxKonOS.Client.Apps.ServerCenter;

/// <summary>SFTP file manager for SSH-only desktops.</summary>
public sealed class SshFileBrowserApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("relaxkonos.ssh-files"), DisplayName: "SSH files",
        Version: "1.0.0", IconGlyph: "📁", Description: "Browse and transfer files over SFTP");

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetRequiredService<SshDesktopSession>();
        if (!session.IsConnected) return;
        var codeEditor = context.Services.GetRequiredService<CodeEditorApp>();
        var imageViewer = context.Services.GetRequiredService<ImageViewerApp>();
        context.ShowWindow(LocalizedText.Get("application.relaxkonos.ssh-files.display_name", "SSH files"),
            new SshFileBrowserView(session,
                path => codeEditor.Manifest.SupportsFile(path) || imageViewer.Manifest.SupportsFile(path),
                path => context.Activations.Activate(RelaxKonOSActivationUris.OpenFile(
                    imageViewer.Manifest.SupportsFile(path) ? imageViewer.Manifest.Id : codeEditor.Manifest.Id, path)).Succeeded),
            bounds: new Rect(150, 100, 860, 580), iconGlyph: Manifest.IconGlyph);
    }
}
