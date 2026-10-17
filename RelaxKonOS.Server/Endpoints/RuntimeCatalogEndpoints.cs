using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ManagedRuntimes;

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
    }
}
