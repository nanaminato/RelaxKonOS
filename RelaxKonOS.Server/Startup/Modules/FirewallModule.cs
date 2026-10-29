using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Firewall;

namespace RelaxKonOS.Server.Startup.Modules;

/// <summary>
/// Host firewall editing. The module only selects the platform implementation; every change still
/// runs through the Core-owned privileged Helper transport and the elevation grant check, so
/// enabling or disabling it never widens the authorization surface.
/// </summary>
internal sealed class FirewallModule : IServerModule
{
    public string Id => ServerModules.FirewallId;
    public IReadOnlyList<string> Capabilities => [ServerCapabilities.Firewall];
    public IReadOnlyList<string> Requires => [];

    public void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        if (OperatingSystem.IsLinux())
            builder.Services.AddSingleton<IHostFirewallService, LinuxUfwFirewallService>();
        else if (OperatingSystem.IsWindows())
            builder.Services.AddSingleton<IHostFirewallService, WindowsFirewallService>();
        else
            builder.Services.AddSingleton<IHostFirewallService, UnavailableHostFirewallService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        // The routes only exist on hosts with a firewall backend; the group additionally re-checks
        // the deployment mode on every request through RequireHostFeature.
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) endpoints.MapFirewallEndpoints();
    }
}
