using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Server.Components;

namespace RelaxKonOS.Server.Endpoints;

public static class PostgreSqlEndpoints
{
    public static IEndpointRouteBuilder MapPostgreSqlEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(PostgreSqlRoutes.State, async (PostgreSqlComponentAdapter component, RelaxKonOS.Server.Databases.PostgreSqlBackupScheduler scheduler, CancellationToken ct) =>
        {
            try { var state=await component.ReadAsync(ct); return Results.Ok(state with {ScheduledOperation=scheduler.Observe(state)}); }
            catch (ComponentLifecycleException ex) { return Results.Problem(statusCode: 503, extensions: new Dictionary<string, object?> { ["code"] = ex.ProblemCode }); }
        }).RequireAuthorization(RuntimeCatalogEndpoints.ReadPolicy);
        app.MapGet(PostgreSqlRoutes.MaintenancePlan, async (RelaxKonOS.Server.Privileged.IPrivilegedOperationTransport helper, CancellationToken ct) =>
        {
            try { return Results.Ok(PostgreSqlComponentAdapter.Decode(await helper.ExecuteAsync(new(RelaxKonOS.Protocol.Privileged.PrivilegedOperationKind.PostgreSqlMaintenancePlan),ct))); }
            catch(ComponentLifecycleException ex) {return Results.Problem(statusCode:503,extensions:new Dictionary<string,object?>{{"code",ex.ProblemCode}});}
        }).RequireAuthorization(RuntimeCatalogEndpoints.ReadPolicy);
        app.MapPost(PostgreSqlRoutes.Credentials, (PostgreSqlCredentialRequest request, HttpContext http,
            RelaxKonOS.Server.Databases.PostgreSqlCredentialStore credentials, RelaxKonOS.Server.Privileged.IHostElevationSessionStore elevations) =>
        {
            if (!InstallationEndpoints.CanInstall(http.User, RelaxKonOS.Protocol.Installations.InstallationServiceId.PostgreSqlAccounts))
                return Results.Problem(statusCode: 403, title: "installation.permission_denied");
            if (!elevations.IsGranted(http.User, RelaxKonOS.Protocol.Privileged.HostElevationCapability.DatabaseAccountsManage, "postgresqlaccounts"))
                return Results.Problem(statusCode: 403, title: "elevation-required");
            return Results.Ok(credentials.Stage(request, InstallationEndpoints.Actor(http.User)));
        }).RequireAuthorization();
        // All mutations use the authenticated, elevated and durable InstallationCoordinator API.
        return app;
    }
}
