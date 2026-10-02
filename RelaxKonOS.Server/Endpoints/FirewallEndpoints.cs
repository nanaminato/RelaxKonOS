using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RelaxKonOS.Protocol.Firewall;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.HostMode;

namespace RelaxKonOS.Server.Endpoints;

public static class FirewallEndpoints
{
    public static IEndpointRouteBuilder MapFirewallEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup($"/{RelaxKonOS.Protocol.Common.RelaxKonOSEndpoints.ApiVersionPrefix}/firewall").RequireAuthorization().WithTags("Firewall").RequireHostFeature(ServerHostFeature.Firewall);
        group.MapGet("/status", (RelaxKonOS.Server.Firewall.IHostFirewallService firewall, CancellationToken ct) => firewall.GetStatusAsync(ct));
        group.MapGet("/rules", async (RelaxKonOS.Server.Firewall.IHostFirewallService firewall, CancellationToken ct) =>
        {
            try { return Results.Ok(await firewall.ListRulesAsync(ct)); }
            catch (RelaxKonOS.Server.Firewall.FirewallRulesUnavailableException error)
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: error.ProblemCode, extensions: new Dictionary<string, object?> { ["problemCode"] = error.ProblemCode });
            }
        });
        group.MapPut("/enabled", (UpdateFirewallEnabledRequest request, HttpContext context, IHostElevationSessionStore elevations, RelaxKonOS.Server.Firewall.IHostFirewallService firewall, ILoggerFactory loggers, CancellationToken ct) =>
            AuthorizeThenRun(context.User, elevations, loggers.CreateLogger("FirewallAudit"), "set-enabled", () => firewall.SetEnabledAsync(request.Enabled, ct)));
        group.MapPut("/defaults", (UpdateFirewallDefaultsRequest request, HttpContext context, IHostElevationSessionStore elevations, RelaxKonOS.Server.Firewall.IHostFirewallService firewall, ILoggerFactory loggers, CancellationToken ct) =>
            AuthorizeThenRun(context.User, elevations, loggers.CreateLogger("FirewallAudit"), "set-defaults", () => firewall.SetDefaultsAsync(request.IncomingPolicy, request.OutgoingPolicy, ct)));
        group.MapPost("/rules", (CreateFirewallRuleRequest request, HttpContext context, IHostElevationSessionStore elevations, RelaxKonOS.Server.Firewall.IHostFirewallService firewall, ILoggerFactory loggers, CancellationToken ct) =>
            AuthorizeThenRun(context.User, elevations, loggers.CreateLogger("FirewallAudit"), "create-rule", () => firewall.CreateRuleAsync(request, ct)));
        group.MapPut("/rules/{number:int}", (int number, UpdateFirewallRuleRequest request, HttpContext context, IHostElevationSessionStore elevations, RelaxKonOS.Server.Firewall.IHostFirewallService firewall, ILoggerFactory loggers, CancellationToken ct) =>
            AuthorizeThenRun(context.User, elevations, loggers.CreateLogger("FirewallAudit"), "update-rule", () => firewall.UpdateRuleAsync(number, request, ct)));
        group.MapDelete("/rules/{number:int}", (int number, HttpContext context, IHostElevationSessionStore elevations, RelaxKonOS.Server.Firewall.IHostFirewallService firewall, ILoggerFactory loggers, CancellationToken ct) =>
            AuthorizeThenRun(context.User, elevations, loggers.CreateLogger("FirewallAudit"), "delete-rule", () => firewall.DeleteRuleAsync(number, ct)));
        return app;
    }

    private static async Task<FirewallOperationResult> AuthorizeThenRun(ClaimsPrincipal user, IHostElevationSessionStore elevations, ILogger logger, string action, Func<Task<FirewallOperationResult>> operation)
    {
        var requester = user.FindFirst(JwtRegisteredClaimNames.Name)?.Value ?? user.FindFirst(ClaimTypes.Name)?.Value ?? string.Empty;
        if (!elevations.IsGranted(user, HostElevationCapability.FirewallChange, "ufw"))
            return new FirewallOperationResult(false, "firewall.elevation_required");
        var result = await operation();
        logger.LogInformation("Firewall change completed. Action={Action}, Requester={Requester}, Success={Success}, Problem={ProblemCode}", action, requester, result.Success, result.ProblemCode);
        return result;
    }
}
