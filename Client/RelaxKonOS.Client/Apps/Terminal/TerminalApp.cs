using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.App.Terminal;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Core.Applications;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Terminal;

public sealed class TerminalApp : RemoteApplicationBase, IOpenTerminalApplication
{
    private readonly TerminalPackageApp _application = new();

    public override ApplicationManifest Manifest => _application.Manifest;

    public override void Activate(AppContext context) =>
        _ = _application.ActivateAsync(CreateContext(context));

    public Task RestoreExistingSessionsAsync(AppContext context, CancellationToken cancellationToken) =>
        _application.RestoreAsync(CreateContext(context), cancellationToken);

    public void OpenTerminal(AppContext context, string workingDirectory) =>
        _application.OpenAt(CreateContext(context), workingDirectory);

    private IExternalAppContext CreateContext(AppContext context) =>
        context.Services.GetRequiredService<ExternalAppContextFactory>().Create(Manifest);
}
