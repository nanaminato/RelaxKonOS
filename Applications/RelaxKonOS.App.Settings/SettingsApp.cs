using RelaxKonOS.AppSDK;
using RelaxKonOS.App.Settings.Views;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.App.Settings;

public interface ISettingsWindow
{
    void Open(Func<ISettingsWorkspace, ManagedWindow> showWorkspace);
    void Navigate(string route);
    void OpenEnvironment();
    void OpenApplicationPermissions(string appId);
}

public sealed class SettingsApp(ISettingsWindow window) : RemoteApplicationBase, IAppActivationHandler
{
    public static AppId Id { get; } = new("relaxkonos.settings");

    public override ApplicationManifest Manifest { get; } = new(
        Id: Id,
        DisplayName: "Settings",
        Version: "1.0.0",
        IconGlyph: "⚙️",
        Description: "Personalization and system settings",
        RequestedPermissions: [AppPermissions.DesktopWallpaperWrite, AppPermissions.ServerCertificatesRead, AppPermissions.ServerCertificatesManage],
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context) => window.Open(workspace =>
    {
        var view = new SettingsWorkspaceView { DataContext = workspace };
        var managedWindow = context.ShowWindow(workspace.Labels("settings.title"), view,
            bounds: new Rect(180, 90, 820, 560), iconGlyph: Manifest.IconGlyph);
        view.AttachWindowHeader(managedWindow);
        return managedWindow;
    });

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
