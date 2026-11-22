using RelaxKonOS.AppSDK;
using RelaxKonOS.App.Settings.Views;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.App.Settings;

public sealed class SettingsApp(ISettingsWindow window) : RemoteApplicationBase, IAppActivationHandler
{
    public static AppId Id { get; } = new("relaxkonos.settings");

    internal static ApplicationManifest SettingsManifest { get; } = new(
        Id: Id,
        DisplayName: "Settings",
        Version: "1.0.0",
        IconGlyph: "⚙️",
        Description: "Personalization and system settings",
        RequestedPermissions: [AppPermissions.DesktopWallpaperWrite, AppPermissions.ServerCertificatesRead, AppPermissions.ServerCertificatesManage],
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override ApplicationManifest Manifest => SettingsManifest;

    public override void Activate(AppContext context) =>
        OpenWorkspace(window, (title, view, bounds) => context.ShowWindow(title, view, bounds));

    internal static void OpenWorkspace(ISettingsWindow window,
        Func<string, SettingsWorkspaceView, Rect, ManagedWindow> showWindow) => window.Open(workspace =>
    {
        var view = new SettingsWorkspaceView { DataContext = workspace };
        var managedWindow = showWindow(workspace.Labels("settings.title"), view, new Rect(180, 90, 820, 560));
        view.AttachWindowHeader(managedWindow);
        return managedWindow;
    });

    public bool CanHandleActivation(Uri uri) => SettingsActivation.Match(uri) is not null;

    public void HandleActivation(AppContext context, AppActivationRequest request, ManagedWindow? existingWindow) =>
        Dispatch(window, request.Uri);

    internal static void Dispatch(ISettingsWindow window, Uri uri)
    {
        var target = SettingsActivation.Match(uri);
        if (target is null) return;

        if (target.ApplicationId is { } appId)
            window.OpenApplicationPermissions(appId);
        else if (target.Route == "system/environment")
            window.OpenEnvironment();
        else
            window.Navigate(target.Route);
    }
}
