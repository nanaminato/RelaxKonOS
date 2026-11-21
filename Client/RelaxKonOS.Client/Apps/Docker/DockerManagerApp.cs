using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.App.Docker;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Core.Applications;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Docker;

public sealed class DockerManagerApp : RemoteApplicationBase
{
    private readonly DockerPackageApp _application = new();

    public override ApplicationManifest Manifest => _application.Manifest;

    public override void Activate(AppContext context)
    {
        var sdk = context.Services.GetRequiredService<ExternalAppContextFactory>().Create(Manifest);
        _ = _application.ActivateAsync(sdk);
    }
}
