using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.WebsitePublishing;

namespace RelaxKonOS.Server.Startup.Modules;

/// <summary>
/// The web server manager and the durable website publication loop.
///
/// Nginx discovery and the marker-owned conf.d integration stay in Core (<c>IWebServerManager</c>),
/// because application deployment publishes reverse-proxy sites through it and the component center
/// reads the Nginx package state. This module owns the management routes and the publication
/// transaction, which binds a site to a certificate: hence the hard dependency on
/// <c>relaxkonos.certificates</c>. The dependency is declared instead of being discovered at request
/// time, so disabling certificates without disabling this module stops startup with a clear message
/// rather than leaving a half-working publication path.
/// </summary>
internal sealed class WebServerModule : IServerModule, IServerModuleDeactivationGuard
{
    public string Id => ServerModules.WebServerId;
    public IReadOnlyList<string> Capabilities => [ServerCapabilities.WebServer];
    public IReadOnlyList<string> Requires => [ServerModules.CertificatesId];

    public void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        builder.Services.AddSingleton<WebsitePublicationStore>();
        builder.Services.AddSingleton<WebsitePublicationCoordinator>();
        builder.Services.AddHostedService(services => services.GetRequiredService<WebsitePublicationCoordinator>());
        builder.Services.AddHttpClient("WebsitePublicationVerification", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
    }

    /// <summary>
    /// A queued or running site publication is only resolvable by this module's coordinator: it
    /// re-checks the site against the certificate and the web server instead of replaying the request.
    /// The ledger is read through the store's read-only view, so asking the question never turns an
    /// operation into "interrupted" by itself.
    /// </summary>
    public ValueTask<IReadOnlyList<ServerModuleOrphanedWork>> InspectOrphanedWorkAsync(
        IServiceProvider services, ServerModuleContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<RelaxKonOS.Protocol.WebsitePublishing.WebsitePublicationOperationDto> pending;
        try
        {
            // The module's own registration is absent while it is disabled, so the ledger is opened
            // directly at the same path the coordinator uses.
            pending = new WebsitePublicationStore(context.Environment).Pending();
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult<IReadOnlyList<ServerModuleOrphanedWork>>(
            [
                new(Id, $"the website publication ledger cannot be read ({exception.GetType().Name}); "
                    + "its state must be established before this module can stay disabled."),
            ]);
        }

        return ValueTask.FromResult<IReadOnlyList<ServerModuleOrphanedWork>>(
            pending.Select(operation => new ServerModuleOrphanedWork(Id,
                $"website publication {operation.OperationId} for domain \"{operation.Domain}\" is still {operation.State} "
                + $"({operation.Stage})."))
                .ToArray());
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        endpoints.MapWebServerEndpoints();
        endpoints.MapWebsitePublicationEndpoints();
    }
}
