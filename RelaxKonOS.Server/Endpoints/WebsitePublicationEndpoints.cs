using System.Security.Claims;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.WebsitePublishing;
using RelaxKonOS.Server.ApplicationDeployments;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.WebsitePublishing;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>One endpoint surface owns publication intent and durable publication history; it never exposes certificate material.</summary>
public static class WebsitePublicationEndpoints
{
    public static IEndpointRouteBuilder MapWebsitePublicationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(WebsitePublicationApiRoutes.Root).RequireAuthorization().WithTags("WebsitePublishing")
            .RequireHostFeature(ServerHostFeature.WebServer);

        group.MapPost(WebsitePublicationApiRoutes.CollectionPattern, (PublishWebsiteRequest request, HttpContext context,
            IHostElevationSessionStore elevations, WebsitePublicationCoordinator coordinator) => Handle(() =>
        {
            if (!elevations.IsGranted(context.User, HostElevationCapability.NginxConfigurationWrite, request.WebServerId))
                return ElevationRequired();
            var operation = coordinator.Start(request, Actor(context.User), Key(context));
            return Results.Accepted(WebsitePublicationApiRoutes.Operation.Replace("{operationId}", operation.OperationId.ToString("D")), operation);
        })).RequireAuthorization(ApplicationDeploymentEndpoints.ManagePolicy);

        group.MapGet(WebsitePublicationApiRoutes.ByApplicationPattern, (Guid applicationId, int? limit, WebsitePublicationCoordinator coordinator) =>
            Handle(() => Results.Ok(coordinator.History(applicationId, limit ?? 20))))
            .RequireAuthorization(ApplicationDeploymentEndpoints.ReadPolicy);

        group.MapGet(WebsitePublicationApiRoutes.OperationPattern, (Guid operationId, WebsitePublicationCoordinator coordinator) =>
            Handle(() => coordinator.Get(operationId) is { } operation ? Results.Ok(operation) : Results.NotFound()))
            .RequireAuthorization(ApplicationDeploymentEndpoints.ReadPolicy);
        return app;
    }

    private static string Key(HttpContext context) => context.Request.Headers["Idempotency-Key"].ToString();
    private static string Actor(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub") ?? throw new UnauthorizedAccessException();
    private static IResult ElevationRequired() => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "需要管理员权限",
        type: "https://relaxkonos.app/problems/elevation-required", extensions: new Dictionary<string, object?> { ["problemCode"] = "elevation-required" });
    private static IResult Handle(Func<IResult> action)
    {
        try { return action(); }
        catch (WebsitePublicationException error)
        {
            return Results.Problem(statusCode: error.StatusCode, title: error.ProblemCode,
                type: "https://relaxkonos.app/problems/" + error.ProblemCode, extensions: new Dictionary<string, object?> { ["problemCode"] = error.ProblemCode });
        }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
    }
}
