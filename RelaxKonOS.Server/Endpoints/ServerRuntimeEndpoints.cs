using System.Security.Claims;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Registry;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Settings;

namespace RelaxKonOS.Server.Endpoints;

public static class ServerRuntimeEndpoints
{
    public static IEndpointRouteBuilder MapServerRuntimeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ServerRuntimeRoutes.Settings, (HttpContext http, IHostElevationSessionStore grants, ServerRuntimeSettingsStore settings) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return grants.IsGranted(http.User, HostElevationCapability.ServerRuntimeConfigure, ServerRuntimeRoutes.ElevationTarget)
                ? Results.Ok(settings.Current) : Denied();
        }).RequireAuthorization();
        app.MapPut(ServerRuntimeRoutes.Settings, (PutServerRuntimeFieldRequest request, HttpContext http, IHostElevationSessionStore grants, ServerRuntimeSettingsStore settings) =>
        {
            if (!grants.IsGranted(http.User, HostElevationCapability.ServerRuntimeConfigure, ServerRuntimeRoutes.ElevationTarget)) return Denied();
            if (!Guid.TryParse(http.User.FindFirstValue("sub") ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor)) return Results.Unauthorized();
            try
            {
                var saved = settings.Save(request, actor);
                return saved is null ? Results.Problem(statusCode: 409, title: "settings.revision_conflict") : Results.Ok(saved);
            }
            catch (ArgumentException) { return Results.BadRequest(new { message = "Invalid server runtime setting." }); }
        }).RequireAuthorization();
        return app;
    }
    private static IResult Denied() => Results.Problem(statusCode: 403,
        type: "https://relaxkonos.app/problems/elevation-required", title: "elevation-required",
        detail: "Server runtime management authorization is required.");
}
