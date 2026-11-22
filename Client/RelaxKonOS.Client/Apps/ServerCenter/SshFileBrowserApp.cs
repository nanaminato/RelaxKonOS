using RelaxKonOS.Client.Localization;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.ServerCenter.Views;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Runtime;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;
using Rect = RelaxKonOS.Core.Primitives.Rect;

namespace RelaxKonOS.Client.Apps.ServerCenter;

/// <summary>SFTP file manager for SSH-only desktops.</summary>
public sealed class SshFileBrowserApp : RemoteApplicationBase, IExternalRemoteApplication
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("relaxkonos.ssh-files"), DisplayName: "SSH files",
        Version: "1.0.0", IconGlyph: "📁", Description: "Browse and transfer files over SFTP");

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetRequiredService<SshDesktopSession>();
        if (!session.IsConnected) return;
        var applications = context.Services.GetRequiredService<ApplicationManager>();
        var opener = new SshFileOpener(applications, () => session.IsConnected);

        var view = new SshFileBrowserView(session, context.Services.GetRequiredService<ISystemLanguage>(),
            new SshTransferPicker(() => UsageMemoryStore.Capture(context)),
            opener.CanOpen, opener.Open);
        AttachWindow(view, context.ShowWindow(WindowTitle, view, WindowBounds, Manifest.IconGlyph));
    }

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.SshFiles is not { IsConnected: true } session || context.SshTransferPicker is not { } picker
            || context.SshFileOpener is not { } opener)
            return Task.CompletedTask;

        var view = new SshFileBrowserView(session, context.SystemLanguage, picker, opener.CanOpen, opener.Open);
        AttachWindow(view, context.Windows.ShowWindow(WindowTitle, view, WindowBounds, Manifest.IconGlyph).Window);
        return Task.CompletedTask;
    }

    private static string WindowTitle => LocalizedText.Get("application.relaxkonos.ssh-files.display_name", "SSH files");
    private static Rect WindowBounds => new(150, 100, 860, 580);

    private static void AttachWindow(SshFileBrowserView view, ManagedWindow window)
    {
        view.AttachWindowHeader(window);
        view.CloseAction = () => Avalonia.Threading.Dispatcher.UIThread.Post(() => window.CloseCommand.Execute(null));
    }
}
