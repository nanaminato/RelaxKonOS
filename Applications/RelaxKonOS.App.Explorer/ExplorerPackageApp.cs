using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.App.Explorer;

public sealed class ExplorerPackageApp : IExternalRemoteApplication, IExternalAppActivationHandler
{
    private static readonly AppId Id = new("relaxkonos.explorer");
    private WeakReference<ManagedWindow>? _activationWindow;
    private bool _activationNeedsLogin;

    public ApplicationManifest Manifest { get; } = new(Id, "RemoteExplorer", "1.0.0", "📁", "Remote file manager",
        [AppPermissions.ServerFilesRead, AppPermissions.ServerFilesWrite]);

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var workspace = RequireWorkspace(context);
        var window = workspace.Open(null);
        _activationNeedsLogin = window is null;
        _activationWindow = new(window ?? ShowLoginPrompt(context, workspace));
        return Task.CompletedTask;
    }

    public bool CanHandleActivation(Uri uri) => RelaxKonOSActivationUris.TryReadExplorerPath(uri, out _);

    public Task HandleActivationAsync(IExternalAppContext context, Uri uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!RelaxKonOSActivationUris.TryReadExplorerPath(uri, out var path)) return Task.CompletedTask;
        var workspace = RequireWorkspace(context);
        var activationWindow = _activationWindow;
        var needsLogin = _activationNeedsLogin;
        _activationWindow = null;
        _activationNeedsLogin = false;
        if (activationWindow?.TryGetTarget(out var window) == true && (needsLogin || workspace.TryNavigate(window, path)))
            return Task.CompletedTask;

        if (workspace.Open(path) is null)
            ShowLoginPrompt(context, workspace);
        return Task.CompletedTask;
    }

    private ManagedWindow ShowLoginPrompt(IExternalAppContext context, IExplorerPackageWorkspace workspace) =>
        context.Windows.ShowWindow(
            workspace.Localize("application.relaxkonos.explorer.display_name"),
            new ExplorerLoginRequiredView(workspace.Localize("explorer.login_required")),
            new Rect(200, 160, 460, 180), Manifest.IconGlyph,
            canResize: false, canMinimize: false, canMaximize: false).Window;

    private static IExplorerPackageWorkspace RequireWorkspace(IExternalAppContext context) =>
        context.AppId == Id && context is IExplorerPackageContext { ExplorerWorkspace: { } workspace }
            ? workspace
            : throw new InvalidOperationException("Explorer requires its official desktop workspace capability.");
}
