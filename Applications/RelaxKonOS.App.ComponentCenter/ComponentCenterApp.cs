using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.App.ComponentCenter;

public sealed class ComponentCenterApp(IComponentCenterWorkspace workspace) : RemoteApplicationBase, IAppActivationHandler
{
    public static AppId Id => ComponentCenterPackageApp.Id;

    public override ApplicationManifest Manifest { get; } =
        ComponentCenterPackageApp.PackageManifest with { IconPath = "avares://RelaxKonOS.Client/Assets/AppIcons/proxy-server.png" };

    public override void Activate(AppContext context) =>
        workspace.Open((title, content) => context.ShowWindow(title, content, ComponentCenterPackageApp.InitialBounds));

    public bool CanHandleActivation(Uri uri) => workspace.CanNavigate(uri);

    public void HandleActivation(AppContext context, AppActivationRequest request, ManagedWindow? existingWindow) =>
        workspace.Navigate(request.Uri);
}
