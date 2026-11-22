using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Runtime;

namespace RelaxKonOS.Client.Services.ServerCenter;

internal sealed class SshFileOpener(ApplicationManager applications, Func<bool> isCurrent) : ISshFileOpener
{
    private static readonly AppId Source = new("relaxkonos.ssh-files");
    private static readonly AppId ImageViewer = new("relaxkonos.imageviewer");
    private static readonly AppId CodeEditor = new("relaxkonos.codeeditor");

    public bool CanOpen(string path) => FindHandler(path) is not null;

    public bool Open(string path) => FindHandler(path) is { } handler
        && applications.Activate(new AppActivationRequest(RelaxKonOSActivationUris.OpenFile(handler, path), Source)).Succeeded;

    private AppId? FindHandler(string path)
    {
        if (!isCurrent()) return null;
        if (applications.SupportsFile(ImageViewer, path)) return ImageViewer;
        if (applications.SupportsFile(CodeEditor, path)) return CodeEditor;
        return null;
    }
}
