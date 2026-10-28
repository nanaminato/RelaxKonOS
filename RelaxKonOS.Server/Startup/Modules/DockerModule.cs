using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Endpoints;

namespace RelaxKonOS.Server.Startup.Modules;

/// <summary>
/// The Docker *manager*: container/image/network/volume editing, compose stacks, the durable stack
/// operation coordinator, engine lifecycle control, the Docker proxy preference UI and the
/// <c>server.docker</c> capability.
///
/// The engine transport itself stays in Core on purpose. Application deployment, the installation
/// domain and the managed-runtime download path all need to reach the Engine without ever showing
/// the manager, and the design forbids a shared behaviour from disappearing because a management
/// surface was disabled. Core therefore keeps <c>IDockerEngineService</c>,
/// <c>IDockerRuntimeInstaller</c>, <c>IDockerProxyResolver</c> and
/// <c>IOutboundProxyHttpClientFactory</c>; this module only adds the management layer on top.
/// </summary>
internal sealed class DockerModule : IServerModule
{
    public string Id => ServerModules.DockerId;
    public IReadOnlyList<string> Capabilities => [ServerCapabilities.Docker];
    public IReadOnlyList<string> Requires => [];

    public void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        builder.Services.AddScoped<RelaxKonOS.Server.ImageMirrors.IDockerImageMirrorResolver,
            RelaxKonOS.Server.ImageMirrors.DockerImageMirrorResolver>();
        builder.Services.Configure<RelaxKonOS.Server.Docker.DockerComposeOptions>(context.Configuration.GetSection("DockerCompose"));
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IDockerComposeService, RelaxKonOS.Server.Docker.DockerComposeService>();
        // Compose stack operations are durable: the ledger outlives the HTTP request, the phone that
        // started it, and this process. The coordinator is the only caller of the executor and
        // reconciles an interrupted operation against the real Engine on startup instead of replaying it.
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.DockerStackOperationStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.DockerStackOperationCoordinator>();
        builder.Services.AddHostedService(services => services.GetRequiredService<RelaxKonOS.Server.Docker.DockerStackOperationCoordinator>());
        // The configurator owns the platform difference internally (systemd drop-in on Linux, Docker
        // Desktop settings on Windows) and reports Unsupported elsewhere.
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IDockerEngineProxyConfigurator, RelaxKonOS.Server.Docker.DockerEngineProxyConfigurator>();
        // Docker Desktop is the only host that hides the real upstream behind an internal relay, so the
        // reader that knows its settings file is selected here instead of probing for a file that cannot
        // exist on the other platforms.
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IDockerDesktopProxyReader>(_ =>
            OperatingSystem.IsWindows()
                ? new RelaxKonOS.Server.Docker.WindowsDockerDesktopProxyReader()
                : new RelaxKonOS.Server.Docker.UnsupportedDockerDesktopProxyReader());
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IDockerProxyService, RelaxKonOS.Server.Docker.DockerProxyService>();
        // Engine lifecycle control reuses the same platform split as the proxy configurator: a systemd
        // unit driven through the privileged Helper, or the Docker Desktop CLI.
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IDockerEngineHostController, RelaxKonOS.Server.Docker.DockerEngineHostController>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Docker.IDockerEngineControlService, RelaxKonOS.Server.Docker.DockerEngineControlService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        endpoints.MapDockerEndpoints();
        endpoints.MapDockerProxyEndpoints();
    }
}
