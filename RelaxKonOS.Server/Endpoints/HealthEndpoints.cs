namespace RelaxKonOS.Server.Endpoints;

/// <summary>Loopback-safe liveness target for the installer-owned Guardian monitor.</summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // This endpoint intentionally carries no account, configuration, or version data.
        // It is nevertheless loopback-only: the Agent is its sole intended caller.
        app.MapGet("/healthz", Health).AllowAnonymous();
        // The User Mode launcher uses this stable readiness path after an atomic version switch.
        // It intentionally exposes no host detail and remains loopback-only like /healthz.
        app.MapGet("/ready", Health).AllowAnonymous();
        return app;
    }

    private static IResult Health(HttpContext context) => context.Connection.RemoteIpAddress is not { } address || System.Net.IPAddress.IsLoopback(address)
        ? Results.Ok(new { status = "healthy" })
        : Results.NotFound();
}
