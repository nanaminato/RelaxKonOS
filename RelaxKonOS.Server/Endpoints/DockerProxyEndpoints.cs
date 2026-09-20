using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RelaxKonOS.Protocol.Docker;
using RelaxKonOS.Server.Docker;
using RelaxKonOS.Server.HostMode;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>
/// Proxy configuration for the local Docker daemon and for the Server's own docker child
/// processes. Both layers are host-wide, so this is one preference for the whole machine rather
/// than a per-user setting; the actor is recorded for auditability only.
/// </summary>
public static class DockerProxyEndpoints
{
    public static IEndpointRouteBuilder MapDockerProxyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(DockerProxyApiRoutes.Proxy)
            .RequireAuthorization()
            .WithTags("Docker")
            .RequireHostFeature(ServerHostFeature.Docker);

        group.MapGet("", (IDockerProxyService service, CancellationToken ct) => service.GetStatusAsync(ct));

        // A rejected preference keeps the previous one in place, so the caller gets a stable problem
        // code instead of a half-applied state. Everything else is reported per layer in the status.
        group.MapPut("", async (SaveDockerProxySettingsRequest request, ClaimsPrincipal principal, IDockerProxyService service, CancellationToken ct) =>
        {
            if (!TryResolveActor(principal, out var actorUserId)) return Results.Unauthorized();
            try
            {
                return Results.Ok(await service.SaveAsync(request, actorUserId, ct));
            }
            catch (DockerProxyValidationException exception)
            {
                return Problem(exception.ProblemCode);
            }
        });

        group.MapDelete("", async (ClaimsPrincipal principal, IDockerProxyService service, CancellationToken ct) =>
        {
            if (!TryResolveActor(principal, out var actorUserId)) return Results.Unauthorized();
            return Results.Ok(await service.ClearAsync(actorUserId, ct));
        });

        return app;
    }

    /// <summary>
    /// The JWT subject is the canonical user id elsewhere in the API, so a principal without a
    /// parseable one is rejected rather than recorded as an anonymous writer.
    /// </summary>
    private static bool TryResolveActor(ClaimsPrincipal principal, out Guid actorUserId)
    {
        var subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(subject, out actorUserId);
    }

    private static IResult Problem(string code) => Results.Problem(statusCode: StatusCodes.Status400BadRequest,
        title: code, detail: code, type: "https://relaxkonos.app/problems/" + code,
        extensions: new Dictionary<string, object?> { ["problemCode"] = code });
}
