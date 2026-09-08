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
        app.MapGet(SettingsApiRoutes.Time, async (HttpContext http, IHostTimeService time, IHostElevationSessionStore grants) =>
            await ExecuteAsync(async () => Results.Ok(new HostTimeSnapshot(await time.ReadAsync(http.RequestAborted),
                new(SettingsOperationCoordinator.TimeResource, SettingsScope.HostMachine),
                new(grants.IsGranted(http.User, HostElevationCapability.HostTimeChange, SettingsOperationCoordinator.TimeResource)
                    ? SettingsCapabilityState.Available : SettingsCapabilityState.ElevationRequired))))).RequireAuthorization();
        app.MapPost(SettingsApiRoutes.TimePreview, async (TimeZonePreviewRequest request, HttpContext http, SettingsOperationCoordinator coordinator) =>
            await ExecuteAsync(async () => Results.Ok(await coordinator.PreviewTimeAsync(http.User, request, http.RequestAborted)))).RequireAuthorization();
        app.MapPost(SettingsApiRoutes.TimeApply, async (SettingsApplyRequest request, HttpContext http, SettingsOperationCoordinator coordinator) =>
            await ExecuteAsync(async () => Results.Ok(await coordinator.ApplyTimeAsync(http.User, request.PlanId, http.RequestAborted)))).RequireAuthorization();
        app.MapGet(SettingsApiRoutes.Operation, async (Guid id, HttpContext http, SettingsOperationCoordinator coordinator) =>
            await ExecuteAsync(async () => Results.Ok(await coordinator.GetAsync(http.User, id, http.RequestAborted)))).RequireAuthorization();
        app.MapPost(SettingsApiRoutes.Rollback, async (Guid id, SettingsRollbackRequest request, HttpContext http, SettingsOperationCoordinator coordinator) =>
            await ExecuteAsync(async () => Results.Ok(await coordinator.RollbackAsync(http.User, id, request, http.RequestAborted)))).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (SettingsException error) { return Results.Problem(statusCode: error.StatusCode, title: error.Code); }
    }
}
