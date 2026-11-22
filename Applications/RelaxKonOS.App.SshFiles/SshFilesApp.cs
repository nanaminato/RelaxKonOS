using Avalonia.Threading;
using RelaxKonOS.UI.SshFiles;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;

namespace RelaxKonOS.App.SshFiles;

public sealed class SshFilesApp : IExternalRemoteApplication
{
    private const string TitleKey = "application.relaxkonos.ssh-files.display_name";
    public ApplicationManifest Manifest { get; } = new(
        Id: new AppId("relaxkonos.ssh-files"), DisplayName: "SSH files",
        Version: "1.0.0", IconGlyph: "📁", Description: "Browse and transfer files over SFTP");

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.SshFiles is not { IsConnected: true } session || context.SshTransferPicker is not { } picker
            || context.SshFileOpener is not { } opener)
            return Task.CompletedTask;

        var view = new SshFileBrowserView(session, context.SystemLanguage, picker, opener.CanOpen, opener.Open);
        var handle = context.Windows.ShowWindow(SshFilesLocalization.Get(context.SystemLanguage, TitleKey), view,
            new Rect(150, 100, 860, 580), Manifest.IconGlyph);
        view.AttachWindowHeader(handle.Window);
        view.CloseAction = () => Dispatcher.UIThread.Post(() => handle.Window.CloseCommand.Execute(null));
        return Task.CompletedTask;
    }
}
