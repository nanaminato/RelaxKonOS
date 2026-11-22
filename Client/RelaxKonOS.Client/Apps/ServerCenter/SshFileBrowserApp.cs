using RelaxKonOS.App.SshFiles.Views;
using RelaxKonOS.App.SshFiles;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Runtime;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;
using Rect = RelaxKonOS.Core.Primitives.Rect;

namespace RelaxKonOS.Client.Apps.ServerCenter;

/// <summary>SFTP file manager for SSH-only desktops.</summary>
public sealed class SshFileBrowserApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest => SshFilesApp.Definition;

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetRequiredService<SshDesktopSession>();
        if (!session.IsConnected) return;
        var applications = context.Services.GetRequiredService<ApplicationManager>();
        var opener = new SshFileOpener(applications, () => session.IsConnected);

        var language = context.Services.GetRequiredService<ISystemLanguage>();
        var view = new SshFileBrowserView(session, language,
            new SshTransferPicker(() => UsageMemoryStore.Capture(context)),
            opener.CanOpen, opener.Open);
        AttachWindow(view, context.ShowWindow(SshFilesLocalization.Get(language, SshFilesApp.TitleKey),
            view, WindowBounds, Manifest.IconGlyph));
    }

    private static Rect WindowBounds => new(150, 100, 860, 580);

    private static void AttachWindow(SshFileBrowserView view, ManagedWindow window)
    {
        view.AttachWindowHeader(window);
        view.CloseAction = () => Avalonia.Threading.Dispatcher.UIThread.Post(() => window.CloseCommand.Execute(null));
    }
}
