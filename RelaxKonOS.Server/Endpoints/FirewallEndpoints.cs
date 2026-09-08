using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RelaxKonOS.Protocol.Firewall;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Endpoints;

public static class FirewallEndpoints
{
    public static IEndpointRouteBuilder MapFirewallEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup($"/{RelaxKonOS.Protocol.Common.RelaxKonOSEndpoints.ApiVersionPrefix}/firewall").RequireAuthorization().WithTags("Firewall");
        group.MapGet("/status", (RelaxKonOS.Server.Firewall.IHostFirewallService firewall, CancellationToken ct) => firewall.GetStatusAsync(ct));
        group.MapGet("/rules", (RelaxKonOS.Server.Firewall.IHostFirewallService firewall, CancellationToken ct) => firewall.ListRulesAsync(ct));
        group.MapPut("/enabled", (UpdateFirewallEnabledRequest request, HttpContext context, IHostElevationSessionStore elevations, RelaxKonOS.Server.Firewall.IFirewallChangeAuthorizationService authorization, RelaxKonOS.Server.Firewall.IHostFirewallService firewall, ILoggerFactory loggers, CancellationToken ct) =>
            AuthorizeThenRun(context.User, elevations, request.CredentialConfirmation, authorization, loggers.CreateLogger("FirewallAudit"), "set-enabled", () => firewall.SetEnabledAsync(request.Enabled, ct)));
        group.MapPut("/defaults", (UpdateFirewallDefaultsRequest request, HttpContext context, IHostElevationSessionStore elevations, RelaxKonOS.Server.Firewall.IFirewallChangeAuthorizationService authorization, RelaxKonOS.Server.Firewall.IHostFirewallService firewall, ILoggerFactory loggers, CancellationToken ct) =>
            AuthorizeThenRun(context.User, elevations, request.CredentialConfirmation, authorization, loggers.CreateLogger("FirewallAudit"), "set-defaults", () => firewall.SetDefaultsAsync(request.IncomingPolicy, request.OutgoingPolicy, ct)));
        group.MapPost("/rules", (CreateFirewallRuleRequest request, HttpContext context, IHostElevationSessionStore elevations, RelaxKonOS.Server.Firewall.IFirewallChangeAuthorizationService authorization, RelaxKonOS.Server.Firewall.IHostFirewallService firewall, ILoggerFactory loggers, CancellationToken ct) =>
            AuthorizeThenRun(context.User, elevations, request.CredentialConfirmation, authorization, loggers.CreateLogger("FirewallAudit"), "create-rule", () => firewall.CreateRuleAsync(request, ct)));
        group.MapPut("/rules/{number:int}", (int number, UpdateFirewallRuleRequest request, HttpContext context, IHostElevationSessionStore elevations, RelaxKonOS.Server.Firewall.IFirewallChangeAuthorizationService authorization, RelaxKonOS.Server.Firewall.IHostFirewallService firewall, ILoggerFactory loggers, CancellationToken ct) =>
            AuthorizeThenRun(context.User, elevations, request.CredentialConfirmation, authorization, loggers.CreateLogger("FirewallAudit"), "update-rule", () => firewall.UpdateRuleAsync(number, request, ct)));
        // DELETE endpoints do not infer request bodies. This operation still needs the
        // credential confirmation, so declare its source explicitly.
        group.MapDelete("/rules/{number:int}", (int number, [Microsoft.AspNetCore.Mvc.FromBody] DeleteFirewallRuleRequest request, HttpContext context, IHostElevationSessionStore elevations, RelaxKonOS.Server.Firewall.IFirewallChangeAuthorizationService authorization, RelaxKonOS.Server.Firewall.IHostFirewallService firewall, ILoggerFactory loggers, CancellationToken ct) =>
            AuthorizeThenRun(context.User, elevations, request.CredentialConfirmation, authorization, loggers.CreateLogger("FirewallAudit"), "delete-rule", () => firewall.DeleteRuleAsync(number, ct)));
        return app;
    }

    private static async Task<FirewallOperationResult> AuthorizeThenRun(ClaimsPrincipal user, IHostElevationSessionStore elevations, FirewallCredentialConfirmation? confirmation,
        RelaxKonOS.Server.Firewall.IFirewallChangeAuthorizationService authorization, ILogger logger, string action, Func<Task<FirewallOperationResult>> operation)
    {
        var requester = user.FindFirst(JwtRegisteredClaimNames.Name)?.Value ?? user.FindFirst(ClaimTypes.Name)?.Value ?? string.Empty;
        if (!elevations.IsGranted(user, HostElevationCapability.FirewallChange, "ufw"))
            return new FirewallOperationResult(false, "firewall.elevation_required");
        var result = authorization.Authorize(requester, confirmation);
        if (!result.Success)
        {
            logger.LogWarning("Firewall change denied. Action={Action}, Requester={Requester}, Problem={ProblemCode}", action, requester, result.ProblemCode);
            return result;
        }
        result = await operation();
        logger.LogInformation("Firewall change completed. Action={Action}, Requester={Requester}, Success={Success}, Problem={ProblemCode}", action, requester, result.Success, result.ProblemCode);
        return result;
    }
}
