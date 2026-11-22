using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.App.Settings;

public interface ISettingsWindow
{
    void Open(AppContext context);
    void Navigate(string route);
    void OpenEnvironment();
    void OpenApplicationPermissions(string appId);
}

public sealed class SettingsApp(ISettingsWindow window) : RemoteApplicationBase, IAppActivationHandler
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("relaxkonos.settings"),
        DisplayName: "Settings",
        Version: "1.0.0",
        IconGlyph: "⚙️",
        Description: "Personalization and system settings",
        RequestedPermissions: [AppPermissions.DesktopWallpaperWrite, AppPermissions.ServerCertificatesRead, AppPermissions.ServerCertificatesManage],
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context) => window.Open(context);

    public bool CanHandleActivation(Uri uri) => SettingsActivation.Match(uri) is not null;

    public void HandleActivation(AppContext context, AppActivationRequest request, ManagedWindow? existingWindow)
    {
        var target = SettingsActivation.Match(request.Uri);
        if (target is null) return;

        if (target.ApplicationId is { } appId)
            window.OpenApplicationPermissions(appId);
        else if (target.Route == "system/environment")
            window.OpenEnvironment();
        else
            window.Navigate(target.Route);
    }
}
