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
internal sealed class TerminalModule : IServerModule
{
    public string Id => ServerModules.TerminalId;
    public IReadOnlyList<string> Capabilities => [ServerCapabilities.Terminal];
    public IReadOnlyList<string> Requires => [];

    public void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        builder.Services.AddSingleton<IPtyFactory, PlatformPtyFactory>();
        builder.Services.AddSingleton<TerminalSessionManager>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context) =>
        endpoints.MapHub<TerminalHub>("/hubs/terminals", options => options.CloseOnAuthenticationExpiration = true);
}
