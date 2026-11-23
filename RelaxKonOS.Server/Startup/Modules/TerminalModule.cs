using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Hubs;
using RelaxKonOS.Server.Terminal;
using RoyalTerminal.Terminal;

namespace RelaxKonOS.Server.Startup.Modules;

/// <summary>
/// Terminal is the pilot module: PTY factory, session manager and Hub are owned together so a
/// disabled module leaves no PTY code reachable. The shared SignalR user identity provider stays in
/// Core because every Hub depends on it.
/// </summary>
internal class TerminalModule : IServerModule, IServerModuleWorkResidency
{
    public RelaxKonOS.Server.Residency.IServerModuleRuntimeFactory RuntimeFactory { get; } = new RelaxKonOS.Server.Residency.ResidentModuleWorkRuntimeFactory(ServerModules.TerminalId);
    public string Id => ServerModules.TerminalId;
    public IReadOnlyList<string> Capabilities => [ServerCapabilities.Terminal];
    public IReadOnlyList<string> Requires => [];

    public virtual void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        builder.Services.AddSingleton<IPtyFactory>(services =>
            ActivatorUtilities.CreateInstance<PlatformPtyFactory>(services, CreateLocalFactory()));
        builder.Services.AddSingleton<TerminalSessionManager>();
        builder.Services.AddHostedService(services => services.GetRequiredService<TerminalSessionManager>());
    }

    public virtual void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context) =>
        endpoints.MapHub<TerminalHub>("/hubs/terminals", options => options.CloseOnAuthenticationExpiration = true);

    private protected virtual IPtyFactory CreateLocalFactory() => BundledTerminalPtyFactory.Create();
}
