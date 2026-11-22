using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;

namespace RelaxKonOS.App.ServerCenter;

public sealed class ServerCenterPackageApp : IExternalRemoteApplication
{
    private static readonly AppId Id = new("relaxkonos.server-center");

    public ApplicationManifest Manifest { get; } = new(
        Id, "Install or manage server", "1.0.0", "🖧", "SSH hosts and RelaxKonOS installation",
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.AppId != Id || context is not IServerCenterPackageContext { ServerCenterWorkspace: { } workspace })
            throw new InvalidOperationException("Server Center requires its official desktop workspace capability.");

        workspace.Open((title, content) => context.Windows.ShowWindow(title, content,
            new Rect(70, 50, 1120, 760), Manifest.IconGlyph).Window);
        return Task.CompletedTask;
    }
}
