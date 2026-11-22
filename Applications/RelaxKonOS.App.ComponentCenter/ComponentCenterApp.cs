using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.App.ComponentCenter;

public interface IComponentCenterWorkspace
{
    void Open(AppContext context);
    bool CanNavigate(Uri uri);
    void Navigate(Uri uri);
}

public sealed class ComponentCenterApp(IComponentCenterWorkspace workspace) : RemoteApplicationBase, IAppActivationHandler
{
    public static AppId Id { get; } = new("relaxkonos.component-center");

    public override ApplicationManifest Manifest { get; } = new(Id, "Component Center", "1.0.0", "⇄", "Managed host components",
        [AppPermissions.ServerComponentsRead, AppPermissions.ServerComponentsManage, AppPermissions.ServerWebServersRead, AppPermissions.ServerWebServersManage, AppPermissions.ServerGitRead, AppPermissions.ServerGitManage],
        InstancePolicy: ApplicationInstancePolicy.SingleWindow,
        IconPath: "avares://RelaxKonOS.Client/Assets/AppIcons/proxy-server.png");

    public override void Activate(AppContext context) => workspace.Open(context);

    public bool CanHandleActivation(Uri uri) => workspace.CanNavigate(uri);

    public void HandleActivation(AppContext context, AppActivationRequest request, ManagedWindow? existingWindow) =>
        workspace.Navigate(request.Uri);
}
