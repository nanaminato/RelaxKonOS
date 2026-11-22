using RelaxKonOS.AppSDK;
using RelaxKonOS.App.Settings.Views;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Core.Applications;

namespace RelaxKonOS.App.Settings;

public sealed class SettingsPackageApp : IExternalRemoteApplication, IExternalAppActivationHandler
{
    public static AppId Id { get; } = new(ISettingsPackageContext.ApplicationId);

    public ApplicationManifest Manifest { get; } = new(
        Id: Id,
        DisplayName: "Settings",
        Version: "1.0.0",
        IconGlyph: "⚙️",
        Description: "Personalization and system settings",
        RequestedPermissions: [AppPermissions.DesktopWallpaperWrite, AppPermissions.ServerCertificatesRead, AppPermissions.ServerCertificatesManage],
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireWorkspace(context).Open(new SettingsDialogs(), workspace =>
        {
            var view = new SettingsWorkspaceView { DataContext = workspace };
            var window = context.Windows.ShowWindow(workspace.Labels("settings.title"), view,
                new Rect(180, 90, 820, 560), Manifest.IconGlyph).Window;
            view.AttachWindowHeader(window);
            return window;
        });
        return Task.CompletedTask;
    }

    public bool CanHandleActivation(Uri uri) => SettingsActivation.Match(uri) is not null;

    public Task HandleActivationAsync(IExternalAppContext context, Uri uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = SettingsActivation.Match(uri);
        if (target is null) return Task.CompletedTask;

        var workspace = RequireWorkspace(context);
        if (target.ApplicationId is { } appId)
            workspace.OpenApplicationPermissions(appId);
        else if (target.Route == "system/environment")
            workspace.OpenEnvironment();
        else
            workspace.Navigate(target.Route);
        return Task.CompletedTask;
    }

    private static ISettingsWindow RequireWorkspace(IExternalAppContext context) =>
        context.AppId == Id && context is ISettingsPackageContext { SettingsWindow: { } workspace }
            ? workspace
            : throw new InvalidOperationException("The Settings package requires the official host workspace capability.");
}
