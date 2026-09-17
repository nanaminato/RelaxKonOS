using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using System.Net;

namespace RelaxKonOS.Server.HostMode;

/// <summary>Defence in depth for endpoint families added after the initial User Mode rollout.</summary>
public sealed class UserModeRequestGuardMiddleware(RequestDelegate next)
{
    private static readonly string[] DisabledPrefixes =
    [
        "/api/v1.0/docker", "/api/v1.0/firewall", "/api/v1.0/file-services",
        "/api/v1.0/webservers", "/api/v1.0/certificates", "/api/v1.0/tunnels", "/api/v1.0/proxy",
        "/api/v1.0/installations", "/api/v1.0/privileged", "/api/v1.0/host-settings",
        "/api/v1.0/settings/catalog", "/api/v1.0/settings/operations",
        AuthApiRoutes.LoginAlias, AuthApiRoutes.SystemLogin
    ];

    public async Task InvokeAsync(HttpContext context, IServerModeResolver mode)
    {
        // User Mode has no reverse-proxy or LAN exception. A null address is the private Unix
        // control socket; every TCP connection carries an address and must be loopback.
        if (mode.Mode == ServerMode.User && context.Connection.RemoteIpAddress is { } address && !IPAddress.IsLoopback(address))
        {
            await Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                type: "https://relaxkonos.app/problems/user-mode-loopback-required",
                title: "User Mode requires loopback",
                detail: "Connect through local loopback or an SSH local forwarding tunnel.").ExecuteAsync(context);
            return;
        }
        if (mode.Mode == ServerMode.User && DisabledPrefixes.Any(prefix => context.Request.Path.StartsWithSegments(prefix)))
        {
            await Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                type: "https://relaxkonos.app/problems/privileged-feature-unavailable",
                title: "Feature unavailable in User Mode",
                detail: "This host operation is unavailable for the current Linux user deployment.").ExecuteAsync(context);
            return;
        }
        await next(context);
    }
}
