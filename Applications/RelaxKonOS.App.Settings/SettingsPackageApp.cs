using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;

namespace RelaxKonOS.App.Settings;

public sealed class SettingsPackageApp : IExternalRemoteApplication, IExternalAppActivationHandler
{
    public ApplicationManifest Manifest => SettingsApp.SettingsManifest;

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SettingsApp.OpenWorkspace(RequireWorkspace(context),
            (title, view, bounds) => context.Windows.ShowWindow(title, view, bounds, Manifest.IconGlyph).Window);
        return Task.CompletedTask;
    }

    public bool CanHandleActivation(Uri uri) => SettingsActivation.Match(uri) is not null;

    public Task HandleActivationAsync(IExternalAppContext context, Uri uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CanHandleActivation(uri))
            SettingsApp.Dispatch(RequireWorkspace(context), uri);
        return Task.CompletedTask;
    }

    private static ISettingsWindow RequireWorkspace(IExternalAppContext context) =>
        context.AppId == SettingsApp.Id && context is ISettingsPackageContext { SettingsWindow: { } workspace }
            ? workspace
            : throw new InvalidOperationException("The Settings package requires the official host workspace capability.");
}
