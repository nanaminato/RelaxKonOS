using RelaxKonOS.Protocol.Settings;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Settings;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.Endpoints;

public static class HostSettingsEndpoints
{
    public static IEndpointRouteBuilder MapHostSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(SettingsApiRoutes.Catalog, (HttpContext http, SettingsCatalog catalog) => Results.Ok(catalog.Read(http.User))).RequireAuthorization();
        app.MapGet(SettingsApiRoutes.Environment, async (string scope, bool? reveal, HttpContext http, IHostEnvironmentService environment) =>
            await ExecuteAsync(async () =>
            {
                http.Response.Headers.CacheControl = "no-store";
                var targetScope = scope switch { "hostUser" => SettingsScope.HostUser, "hostMachine" => SettingsScope.HostMachine,
                    _ => throw new SettingsException(400, "settings.environment.invalid_scope") };
                return Results.Ok(await environment.ReadAsync(http.User, targetScope, reveal == true, http.RequestAborted));
            })).RequireAuthorization();
        app.MapPost(SettingsApiRoutes.EnvironmentPreview, async (EnvironmentPreviewRequest request, HttpContext http, EnvironmentOperationCoordinator coordinator) =>
            await ExecuteAsync(async () => Results.Ok(await coordinator.PreviewAsync(http.User, request, http.RequestAborted)))).RequireAuthorization();
        app.MapPost(SettingsApiRoutes.EnvironmentApply, async (SettingsApplyRequest request, HttpContext http, EnvironmentOperationCoordinator coordinator) =>
            await ExecuteAsync(async () => Results.Ok(await coordinator.ApplyAsync(http.User, request.PlanId, http.RequestAborted)))).RequireAuthorization();
        app.MapGet(SettingsApiRoutes.Time, async (HttpContext http, IHostTimeService time, IHostElevationSessionStore grants) =>
            await ExecuteAsync(async () => Results.Ok(new HostTimeSnapshot(await time.ReadAsync(http.RequestAborted),
                new(SettingsOperationCoordinator.TimeResource, SettingsScope.HostMachine),
                new(grants.IsGranted(http.User, HostElevationCapability.HostTimeChange, SettingsOperationCoordinator.TimeResource)
                    ? SettingsCapabilityState.Available : SettingsCapabilityState.ElevationRequired))))).RequireAuthorization();
        app.MapPost(SettingsApiRoutes.TimePreview, async (TimeZonePreviewRequest request, HttpContext http, SettingsOperationCoordinator coordinator) =>
            await ExecuteAsync(async () => Results.Ok(await coordinator.PreviewTimeAsync(http.User, request, http.RequestAborted)))).RequireAuthorization();
        app.MapPost(SettingsApiRoutes.TimeApply, async (SettingsApplyRequest request, HttpContext http, SettingsOperationCoordinator coordinator) =>
            await ExecuteAsync(async () => Results.Ok(await coordinator.ApplyTimeAsync(http.User, request.PlanId, http.RequestAborted)))).RequireAuthorization();
        app.MapGet(SettingsApiRoutes.Operation, async (Guid id, HttpContext http, SettingsOperationCoordinator coordinator, EnvironmentOperationCoordinator environment) =>
            await ExecuteAsync(async () => Results.Ok(await environment.GetIfExistsAsync(http.User, id, http.RequestAborted)
                ?? await coordinator.GetAsync(http.User, id, http.RequestAborted)))).RequireAuthorization();
        app.MapPost(SettingsApiRoutes.Rollback, async (Guid id, SettingsRollbackRequest request, HttpContext http, SettingsOperationCoordinator coordinator, EnvironmentOperationCoordinator environment) =>
            await ExecuteAsync(async () => Results.Ok(await environment.RollbackIfExistsAsync(http.User, id, request, http.RequestAborted)
                ?? await coordinator.RollbackAsync(http.User, id, request, http.RequestAborted)))).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (SettingsException error) { return Results.Problem(statusCode: error.StatusCode, title: error.Code); }
    }
}
