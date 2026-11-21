using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.App.Browser;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Core.Applications;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Browser;

public sealed class BrowserApp : RemoteApplicationBase
{
    private readonly BrowserPackageApp _entry = new();

    public override ApplicationManifest Manifest => _entry.Manifest;

    public override void Activate(AppContext context) =>
        _entry.ActivateAsync(context.Services.GetRequiredService<ExternalAppContextFactory>().Create(Manifest))
            .GetAwaiter().GetResult();
}
