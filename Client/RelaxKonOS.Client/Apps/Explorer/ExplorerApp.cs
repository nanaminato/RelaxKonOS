using RelaxKonOS.App.Explorer;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Runtime;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Explorer;

public sealed class ExplorerApp : RemoteApplicationBase, IAppActivationHandler
{
    private readonly ExplorerHostWorkspace _workspace = new();

    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("relaxkonos.explorer"),
        DisplayName: "RemoteExplorer",
        Version: "1.0.0",
        IconGlyph: "📁",
        Description: "Remote file manager",
        RequestedPermissions: [AppPermissions.ServerFilesRead, AppPermissions.ServerFilesWrite]);

    public override void Activate(AppContext context) => OpenExplorer(context, null);

    public bool CanHandleActivation(Uri uri) => RelaxKonOSActivationUris.TryReadExplorerPath(uri, out _);

    public void HandleActivation(AppContext context, AppActivationRequest request, ManagedWindow? existingWindow)
    {
        if (!RelaxKonOSActivationUris.TryReadExplorerPath(request.Uri, out var path)) return;
        if (!_workspace.TryNavigate(existingWindow, path)) OpenExplorer(context, path);
    }

    private void OpenExplorer(AppContext context, string? initialPath)
    {
        if (_workspace.TryOpen(context, initialPath, Manifest.IconGlyph)) return;

        var stub = new ExplorerLoginRequiredView(LocalizedText.Get("explorer.login_required"));
        context.ShowWindow(LocalizedText.Get("application.relaxkonos.explorer.display_name"), stub,
            bounds: new Rect(200, 160, 460, 180), iconGlyph: Manifest.IconGlyph,
            canResize: false, canMinimize: false, canMaximize: false);
    }
}
