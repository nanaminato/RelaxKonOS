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

    public bool CanHandleActivation(Uri uri)
    {
        if (!uri.Scheme.Equals("relaxkonos", StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("settings", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = GetPathSegments(uri);
        return (segments.Length == 1 && new[] { "home", "system", "account-security", "personalization", "time-language", "network", "apps", "default-apps", "developer", "accessibility", "about" }.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
               || (segments.Length == 2 && segments[0].Equals("system", StringComparison.OrdinalIgnoreCase)
                   && new[] { "preferences", "environment" }.Contains(segments[1], StringComparer.OrdinalIgnoreCase))
               || (segments.Length == 2 && segments[0].Equals("personalization", StringComparison.OrdinalIgnoreCase)
                   && new[] { "colors", "style", "layout", "background" }.Contains(segments[1], StringComparer.OrdinalIgnoreCase))
               || (segments.Length == 3 && segments[0].Equals("apps", StringComparison.OrdinalIgnoreCase)
                   && segments[2].Equals("permissions", StringComparison.OrdinalIgnoreCase)
                   && !string.IsNullOrWhiteSpace(segments[1]));
    }

    public void HandleActivation(AppContext context, AppActivationRequest request, ManagedWindow? existingWindow)
    {
        var segments = GetPathSegments(request.Uri);
        if (segments.Length == 2 && segments[0].Equals("system", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("environment", StringComparison.OrdinalIgnoreCase))
            window.OpenEnvironment();
        else if (segments.Length is 1 or 2)
            window.Navigate(string.Join("/", segments));
        else if (segments.Length == 3 && segments[0].Equals("apps", StringComparison.OrdinalIgnoreCase)
                 && segments[2].Equals("permissions", StringComparison.OrdinalIgnoreCase))
            window.OpenApplicationPermissions(segments[1]);
    }

    private static string[] GetPathSegments(Uri uri) => uri.AbsolutePath
        .Split('/', StringSplitOptions.RemoveEmptyEntries)
        .Select(Uri.UnescapeDataString)
        .ToArray();

}
