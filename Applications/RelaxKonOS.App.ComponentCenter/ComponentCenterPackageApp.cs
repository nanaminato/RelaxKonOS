using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;

namespace RelaxKonOS.App.ComponentCenter;

public sealed class ComponentCenterPackageApp : IExternalRemoteApplication, IExternalAppActivationHandler
{
    public static AppId Id { get; } = new("relaxkonos.component-center");
    internal static Rect InitialBounds { get; } = new(80, 45, 1060, 800);

    internal static ApplicationManifest PackageManifest { get; } = new(Id, "Component Center", "1.0.0", "⇄",
        "Managed host components",
        [AppPermissions.ServerComponentsRead, AppPermissions.ServerComponentsManage,
            AppPermissions.ServerWebServersRead, AppPermissions.ServerWebServersManage,
            AppPermissions.ServerGitRead, AppPermissions.ServerGitManage],
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public ApplicationManifest Manifest => PackageManifest;

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireWorkspace(context).Open(ComponentCenterLayout.Create, (title, content) => context.Windows.ShowWindow(title, content,
            InitialBounds, Manifest.IconGlyph).Window);
        return Task.CompletedTask;
    }

    public bool CanHandleActivation(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == "relaxkonos" && uri.Host == "components" && uri.Port == -1
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.OriginalString.Length <= 4096
        && uri.IsWellFormedOriginalString() && uri.AbsolutePath.Length > 1;

    public Task HandleActivationAsync(IExternalAppContext context, Uri uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CanHandleActivation(uri))
        {
            var workspace = RequireWorkspace(context);
            if (workspace.CanNavigate(uri)) workspace.Navigate(uri);
        }
        return Task.CompletedTask;
    }

    private static IComponentCenterWorkspace RequireWorkspace(IExternalAppContext context) =>
        context.AppId == Id && context is IComponentCenterPackageContext { ComponentCenterWorkspace: { } workspace }
            ? workspace
            : throw new InvalidOperationException("The Component Center package requires the official host workspace capability.");
}
