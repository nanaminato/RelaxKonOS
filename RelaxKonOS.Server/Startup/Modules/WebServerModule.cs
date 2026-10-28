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
internal sealed class WebServerModule : IServerModule
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

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        endpoints.MapWebServerEndpoints();
        endpoints.MapWebsitePublicationEndpoints();
    }
}
