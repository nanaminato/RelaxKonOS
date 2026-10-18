using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Protocol.Installations;
using Microsoft.AspNetCore.Mvc;

namespace RelaxKonOS.Server.Endpoints;

public static class RuntimeCatalogEndpoints
{
    public const string ReadPolicy = "RuntimeRead";
    public const string ManagePolicy = "RuntimeManage";

    public static IEndpointRouteBuilder MapRuntimeCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("").RequireHostFeature(ServerHostFeature.PrivilegedOperations).WithTags("Managed runtimes");
        group.MapGet(RuntimeRoutes.Sources, async (RuntimeCatalogService service, CancellationToken ct) =>
            await RespondAsync(async () => Results.Ok(await service.SourcesAsync(ct))))
            .RequireAuthorization(ReadPolicy);
        group.MapGet(RuntimeRoutes.Catalog, async (string sourceId, bool? includePrerelease, RuntimeCatalogService service, CancellationToken ct) =>
            await RespondAsync(async () => Results.Ok(await service.CatalogAsync(sourceId, false, includePrerelease ?? false, ct))))
            .RequireAuthorization(ReadPolicy);
        group.MapPost(RuntimeRoutes.Refresh, async (string sourceId, bool? includePrerelease, RuntimeCatalogService service, CancellationToken ct) =>
            await RespondAsync(async () => Results.Ok(await service.CatalogAsync(sourceId, true, includePrerelease ?? false, ct))))
            .RequireAuthorization(ManagePolicy);
        group.MapGet(RuntimeRoutes.SingBoxInstallation, async ([FromServices] SingBoxRuntimeService service, CancellationToken ct) =>
            await RespondAsync(async () => Results.Ok(await service.QueryAsync(ct))))
            .RequireAuthorization(ReadPolicy);
        group.MapPost(RuntimeRoutes.SingBoxPlan, async (RuntimePlanSelection request, HttpContext http,
            [FromServices] SingBoxRuntimeService service, CancellationToken ct) =>
        {
            if (!InstallationEndpoints.CanInstall(http.User, InstallationServiceId.SingBox)) return Results.Forbid();
            if (request.Request is null || string.IsNullOrWhiteSpace(request.SourceId)) return Results.BadRequest();
            return await RespondAsync(async () => Results.Ok(await service.PlanAsync(request.SourceId, request.Request, ct)));
        }).RequireAuthorization(ManagePolicy);
        return app;
    }

    private static async Task<IResult> RespondAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (RuntimeCatalogException ex)
        {
            return Results.Problem(statusCode: ex.ProblemCode switch
            {
                "runtime.source_not_found" => 404,
                "runtime.source_invalid" => 400,
                "runtime.source_store_busy" => 409,
                _ => 503
            }, extensions: new Dictionary<string, object?> { ["code"] = ex.ProblemCode });
        }
        catch (RuntimeLifecycleException ex)
        {
            return Results.Problem(statusCode: ex.ProblemCode switch
            {
                "runtime.source_not_found" => 404,
                "runtime.source_invalid" or "runtime.request_invalid" => 400,
                "runtime.instance_busy" or "runtime.recovery_required" => 409,
                "runtime.platform_unsupported" => 403,
                _ => 503
            }, extensions: new Dictionary<string, object?> { ["code"] = ex.ProblemCode });
        }
    }
}
