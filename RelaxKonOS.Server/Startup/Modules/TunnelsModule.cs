using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Tunnels;

namespace RelaxKonOS.Server.Startup.Modules;

internal class TunnelsModule : IServerModule
{
    internal const string ModuleId = "relaxkonos.tunnels";
    public string Id => ModuleId;
    public IReadOnlyList<string> Capabilities => [ServerCapabilities.Tunnels];
    public IReadOnlyList<string> Requires => [];

    private protected virtual CollectibleTunnelExecution LoadExecution() => new();

    public virtual void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        builder.Services.AddSingleton(_ => LoadExecution());
        builder.Services.AddSingleton(sp => sp.GetRequiredService<CollectibleTunnelExecution>().Module);
    }

    public virtual void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        if (context.StorageProvider == "sqlite") endpoints.MapTunnelEndpoints();
    }
}
