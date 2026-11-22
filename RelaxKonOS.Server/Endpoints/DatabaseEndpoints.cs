using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Server.Databases;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.ServerModule.Database;

namespace RelaxKonOS.Server.Endpoints;

internal static class DatabaseEndpoints
{
    internal static void MapDatabaseCredentialEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPostgreSqlCredentialEndpoint();
        endpoints.MapPost(MySqlRoutes.Credentials, async (
            PostgreSqlCredentialRequest request, HttpContext http,
            DatabaseQueries queries,
            PostgreSqlCredentialStore credentials,
            IHostElevationSessionStore elevations, CancellationToken ct) =>
        {
            var id = InstallationServiceId.MySqlAccounts;
            if (!InstallationEndpoints.CanInstall(http.User, id)) return Results.Forbid();
            if (!elevations.IsGranted(http.User, InstallationEndpoints.Capability(id), InstallationEndpoints.Target(id)))
                return Results.Problem(statusCode: 403, title: "elevation-required");
            if (!MySqlAccountCommand.Password(request.Password))
                return Results.Problem(statusCode: 400, title: "database.credential_invalid");
            var state = await queries.ReadMySqlAsync(ct);
            if (state.InstanceId != request.InstanceId || state.PendingOperationId is not null)
                return Results.Problem(statusCode: 409, title: "database.instance_invalid");
            return Results.Ok(credentials.Stage(request, InstallationEndpoints.Actor(http.User)));
        }).RequireAuthorization();
    }

    internal static void MapDatabaseObservationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPostgreSqlObservationEndpoints();
        endpoints.MapGet(MySqlRoutes.State,
            async (DatabaseQueries queries, CancellationToken ct) =>
            {
                try { return Results.Ok(await queries.ReadMySqlAsync(ct)); }
                catch (InstallationException)
                { return Results.Problem(statusCode: 503); }
            }).RequireAuthorization(RuntimeCatalogEndpoints.ReadPolicy);
    }
}
