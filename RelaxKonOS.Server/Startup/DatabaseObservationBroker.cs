using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Components;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.ServerModule.Database.Contracts;

namespace RelaxKonOS.Server.Startup;

/// <summary>Fixed read operations for the trusted database module on the private broker listener.</summary>
internal static class DatabaseObservationBroker
{
    internal static void Map(WebApplication app, IServiceProvider services,
        Func<ModuleHostConnection, string, ClaimsPrincipal?> resolveUser)
    {
        app.MapGet(DatabaseObservationProtocol.PostgreSqlState, (HttpContext http, Guid? instance) =>
            ReadAsync(http, services, resolveUser, (queries, token) => queries.ReadPostgreSqlAsync(instance, token)));
        app.MapGet(DatabaseObservationProtocol.PostgreSqlMaintenancePlan, (Delegate)((HttpContext http) =>
            ReadAsync(http, services, resolveUser, (queries, token) => queries.ReadPostgreSqlMaintenancePlanAsync(token))));
        app.MapGet(DatabaseObservationProtocol.PostgreSqlMigrationPlan, (Delegate)((HttpContext http) =>
            ReadAsync(http, services, resolveUser, (queries, token) => queries.ReadPostgreSqlMigrationPlanAsync(token))));
        app.MapGet(DatabaseObservationProtocol.MySqlState, (Delegate)((HttpContext http) =>
            ReadAsync(http, services, resolveUser, (queries, token) => queries.ReadMySqlAsync(token))));
    }

    private static async Task<IResult> ReadAsync<T>(HttpContext http, IServiceProvider services,
        Func<ModuleHostConnection, string, ClaimsPrincipal?> resolveUser,
        Func<IDatabaseQueries, CancellationToken, Task<T>> read)
    {
        var hosts = services.GetRequiredService<ModuleHostSupervisor>();
        using var caller = hosts.AcquireAuthenticated(http.Request.Headers[ModuleHostProtocol.ServiceTokenHeader].ToString(),
                DatabaseObservationProtocol.ModuleId);
        if (caller is null || resolveUser(caller, http.Request.Headers[ModulePrivilegeProtocol.RequestContextHeader].ToString()) is not { } user
            || !hosts.CanAccessDatabaseObservation(user))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        using var scope = services.CreateScope();
        if (!scope.ServiceProvider.GetRequiredService<HostElevationSessionState>().IsSessionValid(user)
            || !(await scope.ServiceProvider.GetRequiredService<IAuthorizationService>()
                .AuthorizeAsync(user, RuntimeCatalogEndpoints.ReadPolicy)).Succeeded)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var queries = services.GetService<IDatabaseQueries>();
        if (queries is null)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, caller.Stopping);
        try
        {
            var result = await read(queries, stopping.Token);
            stopping.Token.ThrowIfCancellationRequested();
            await http.Response.WriteAsJsonAsync(result, RelaxKonOSJsonOptions.Default, stopping.Token);
            return Results.Empty;
        }
        catch (ComponentLifecycleException error)
        {
            return Results.Problem(statusCode: 503, extensions: new Dictionary<string, object?> { ["code"] = error.ProblemCode });
        }
        catch (InstallationException)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (caller.Stopping.IsCancellationRequested && !http.RequestAborted.IsCancellationRequested)
        {
            if (!http.Response.HasStarted) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            http.Abort();
            return Results.Empty;
        }
    }
}
