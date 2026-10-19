using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;
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
            if (request.Request is null) return Results.BadRequest();
            // Only removal may omit the source; it must still be a valid identifier when one is given.
            if (string.IsNullOrWhiteSpace(request.SourceId)
                ? request.Request.Change != RuntimeChange.Uninstall
                : !RuntimeValidation.Identifier(request.SourceId)) return Results.BadRequest();
            return await RespondAsync(async () => Results.Ok(await service.PlanAsync(request.SourceId, request.Request, ct)));
        }).RequireAuthorization(ManagePolicy);
        group.MapGet(RuntimeRoutes.MihomoInstallation, async ([FromServices] MihomoRuntimeService service, CancellationToken ct) =>
            await RespondAsync(async () => Results.Ok(await service.QueryAsync(ct))))
            .RequireAuthorization(ReadPolicy);
        group.MapPost(RuntimeRoutes.MihomoPlan, async (RuntimePlanSelection request, HttpContext http,
            [FromServices] MihomoRuntimeService service, CancellationToken ct) =>
        {
            if (!InstallationEndpoints.CanInstall(http.User, InstallationServiceId.Mihomo)) return Results.Forbid();
            if (request.Request is null || (string.IsNullOrWhiteSpace(request.SourceId)
                ? request.Request.Change != RuntimeChange.Uninstall : !RuntimeValidation.Identifier(request.SourceId)))
                return Results.BadRequest();
            return await RespondAsync(async () => Results.Ok(await service.PlanAsync(request.SourceId, request.Request, ct)));
        }).RequireAuthorization(ManagePolicy);
        group.MapGet(RuntimeRoutes.MihomoRecovery, async ([FromServices] MihomoRuntimeService service, CancellationToken ct) =>
            await RespondAsync(async () => Results.Ok(await service.RecoveryAsync(ct))))
            .RequireAuthorization(ReadPolicy);
        group.MapPost(RuntimeRoutes.MihomoRecoveryRetry, async (RuntimeRecoveryActionRequest request, HttpContext http,
            [FromServices] MihomoRuntimeRecoveryService service, [FromServices] IHostElevationSessionStore elevations, CancellationToken ct) =>
        {
            if (!MihomoHostTarget(http, elevations)) return ElevationRequired();
            if (request.OperationId == Guid.Empty) return Results.BadRequest();
            return await RespondAsync(async () => Results.Ok(await service.RetryAsync(request.OperationId, ct)));
        }).RequireAuthorization(ManagePolicy);
        group.MapPost(RuntimeRoutes.MihomoRecoveryStop, async (RuntimeRecoveryActionRequest request, HttpContext http,
            [FromServices] MihomoRuntimeService service, [FromServices] IHostElevationSessionStore elevations, CancellationToken ct) =>
        {
            if (!MihomoHostTarget(http, elevations)) return ElevationRequired();
            if (request.OperationId == Guid.Empty) return Results.BadRequest();
            return await RespondAsync(async () => Results.Ok(await service.StopRecoveryAsync(request.OperationId, ct)));
        }).RequireAuthorization(ManagePolicy);
        group.MapPost(RuntimeRoutes.MihomoRecoveryClose, async (RuntimeRecoveryActionRequest request, HttpContext http,
            [FromServices] MihomoRuntimeService service, [FromServices] IHostElevationSessionStore elevations, CancellationToken ct) =>
        {
            if (!MihomoHostTarget(http, elevations)) return ElevationRequired();
            if (request.OperationId == Guid.Empty) return Results.BadRequest();
            return await RespondAsync(async () => Results.Ok(await service.CloseRecoveryAsync(request.OperationId, request.Confirmed, ct)));
        }).RequireAuthorization(ManagePolicy);
        group.MapPost(RuntimeRoutes.MihomoResources, async (HttpContext http,
            [FromServices] MihomoRuntimeService service, [FromServices] IHostElevationSessionStore elevations, CancellationToken ct) =>
        {
            if (!MihomoHostTarget(http, elevations)) return ElevationRequired();
            return await RespondAsync(async () => Results.Ok(await service.PrepareResourcesAsync(ct)));
        }).RequireAuthorization(ManagePolicy);
        group.MapGet(RuntimeRoutes.SingBoxRecovery, async ([FromServices] SingBoxRuntimeService service, CancellationToken ct) =>
            await RespondAsync(async () => Results.Ok(await service.RecoveryAsync(ct))))
            .RequireAuthorization(ReadPolicy);
        group.MapPost(RuntimeRoutes.SingBoxRecoveryRetry, async (RuntimeRecoveryActionRequest request, HttpContext http,
            [FromServices] SingBoxRuntimeService service, [FromServices] IHostElevationSessionStore elevations, CancellationToken ct) =>
        {
            if (!RecoveryTarget(http, elevations)) return ElevationRequired();
            if (request.OperationId == Guid.Empty) return Results.BadRequest();
            return await RespondAsync(async () =>
            {
                // Retrying replays the Host's own compensation; it never prepares a revision or new credentials.
                var receipt = await service.RecoverAsync(Guid.NewGuid(), request.OperationId, ct)
                    ?? throw new RuntimeLifecycleException("runtime.recovery_absent");
                return Results.Ok(receipt);
            });
        }).RequireAuthorization(ManagePolicy);
        group.MapPost(RuntimeRoutes.SingBoxRecoveryStop, async (RuntimeRecoveryActionRequest request, HttpContext http,
            [FromServices] SingBoxRuntimeService service, [FromServices] IHostElevationSessionStore elevations, CancellationToken ct) =>
        {
            if (!RecoveryTarget(http, elevations)) return ElevationRequired();
            if (request.OperationId == Guid.Empty) return Results.BadRequest();
            return await RespondAsync(async () => Results.Ok(await service.StopRecoveryAsync(request.OperationId, ct)));
        }).RequireAuthorization(ManagePolicy);
        group.MapPost(RuntimeRoutes.SingBoxRecoveryClose, async (RuntimeRecoveryActionRequest request, HttpContext http,
            [FromServices] SingBoxRuntimeService service, [FromServices] IHostElevationSessionStore elevations, CancellationToken ct) =>
        {
            if (!RecoveryTarget(http, elevations)) return ElevationRequired();
            if (request.OperationId == Guid.Empty) return Results.BadRequest();
            return await RespondAsync(async () => Results.Ok(await service.CloseRecoveryAsync(request.OperationId, request.Confirmed, ct)));
        }).RequireAuthorization(ManagePolicy);
        return app;
    }

    /// <summary>Recovery mutates privileged host state, so it needs both the install claim and a live elevation.</summary>
    private static bool RecoveryTarget(HttpContext http, IHostElevationSessionStore elevations) =>
        InstallationEndpoints.CanInstall(http.User, InstallationServiceId.SingBox)
        && elevations.IsGranted(http.User, InstallationEndpoints.Capability(InstallationServiceId.SingBox),
            InstallationEndpoints.Target(InstallationServiceId.SingBox));

    /// <summary>Every Mihomo host write shares one claim and one elevation target; neither is inferred from the other runtime.</summary>
    private static bool MihomoHostTarget(HttpContext http, IHostElevationSessionStore elevations) =>
        InstallationEndpoints.CanInstall(http.User, InstallationServiceId.Mihomo)
        && elevations.IsGranted(http.User, InstallationEndpoints.Capability(InstallationServiceId.Mihomo),
            InstallationEndpoints.Target(InstallationServiceId.Mihomo));

    private static IResult ElevationRequired() => Results.Problem(statusCode: 403,
        extensions: new Dictionary<string, object?> { ["code"] = "elevation-required" });

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
                "runtime.source_invalid" or "runtime.request_invalid" or "runtime.recovery_confirmation_required" => 400,
                "runtime.instance_busy" or "runtime.recovery_required" or "runtime.recovery_absent"
                    or "runtime.recovery_target_conflict" or "runtime.recovery_not_required"
                    or "runtime.recovery_stop_required" or "runtime.recovery_stop_failed"
                    or "runtime.publication_unconfirmed" or "runtime.publication_state_invalid"
                    or "runtime.installation_absent"
                    or "runtime.external_installation" or "runtime.state_unknown" => 409,
                "runtime.platform_unsupported" => 403,
                _ => 503
            }, extensions: new Dictionary<string, object?> { ["code"] = ex.ProblemCode });
        }
    }
}
