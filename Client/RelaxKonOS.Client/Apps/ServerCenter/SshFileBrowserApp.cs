using RelaxKonOS.Client.Localization;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.ServerCenter.Views;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.Apps.CodeEditor;
using RelaxKonOS.Runtime;
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
        var applications = context.Services.GetRequiredService<ApplicationManager>();
        var imageViewerId = new AppId("relaxkonos.imageviewer");
        var view = new SshFileBrowserView(session,
                path => codeEditor.Manifest.SupportsFile(path) || applications.SupportsFile(imageViewerId, path),
                path => context.Activations.Activate(RelaxKonOSActivationUris.OpenFile(
                    applications.SupportsFile(imageViewerId, path) ? imageViewerId : codeEditor.Manifest.Id, path)).Succeeded);
        var window = context.ShowWindow(LocalizedText.Get("application.relaxkonos.ssh-files.display_name", "SSH files"), view,
            bounds: new Rect(150, 100, 860, 580), iconGlyph: Manifest.IconGlyph);
        view.AttachWindowHeader(window);
        view.CloseAction = () => Avalonia.Threading.Dispatcher.UIThread.Post(() => context.WindowManager.Close(window));
    }
}
