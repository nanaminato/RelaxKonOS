namespace RelaxKonOS.Server.Endpoints;

/// <summary>Loopback-safe liveness target for the installer-owned Guardian monitor.</summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // This endpoint intentionally carries no account, configuration, or version data.
        // It is nevertheless loopback-only: the Agent is its sole intended caller.
        app.MapGet("/healthz", (HttpContext context) =>
            context.Connection.RemoteIpAddress is { } address && System.Net.IPAddress.IsLoopback(address)
                ? Results.Ok(new { status = "healthy" })
                : Results.NotFound()).AllowAnonymous();
        return app;
    }
}
