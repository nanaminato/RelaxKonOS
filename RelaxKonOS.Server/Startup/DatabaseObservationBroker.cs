using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Server.Components;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.ServerModule.Database.Contracts;

namespace RelaxKonOS.Server.Startup;

/// <summary>Private observation and backup scheduling for the trusted database module.</summary>
internal static class DatabaseObservationBroker
{
    internal static void Map(WebApplication app, IServiceProvider services,
        Func<ModuleHostConnection, string, ClaimsPrincipal?> resolveUser)
    {
        app.MapGet(DatabaseObservationProtocol.PostgreSqlState, (HttpContext http, Guid? instance) =>
            ReadAsync<IDatabaseQueryHost, PostgreSqlObservationSnapshot>(http, services, resolveUser,
                async (queries, token) => PostgreSqlObservation.Capture(
                    await queries.ReadPostgreSqlAsync(instance, token),
                    services.GetRequiredService<IPostgreSqlBackupLedger>())));
        app.MapGet(DatabaseObservationProtocol.PostgreSqlMaintenancePlan, (Delegate)((HttpContext http) =>
            ReadAsync<IDatabaseQueryHost, PostgreSqlState>(http, services, resolveUser,
                (queries, token) => queries.ReadPostgreSqlMaintenancePlanAsync(token))));
        app.MapGet(DatabaseObservationProtocol.PostgreSqlMigrationPlan, (Delegate)((HttpContext http) =>
            ReadAsync<IDatabaseQueryHost, PostgreSqlState>(http, services, resolveUser,
                (queries, token) => queries.ReadPostgreSqlMigrationPlanAsync(token))));
        app.MapGet(DatabaseObservationProtocol.MySqlState, (Delegate)((HttpContext http) =>
            ReadAsync<IDatabaseQueryHost, MySqlState>(http, services, resolveUser,
                (queries, token) => queries.ReadMySqlAsync(token))));
        app.MapGet(DatabaseObservationProtocol.PostgreSqlBackupSlot, (Delegate)((HttpContext http) =>
            ScheduleAsync(http, services, (host, token) => host.ReadDueSlotAsync(token))));
        app.MapPost(DatabaseObservationProtocol.PostgreSqlBackupSlot,
            (HttpContext http, Guid instanceId, Guid scheduleId, DateTimeOffset dueAt) =>
                ScheduleAsync(http, services,
                    (host, token) => host.TryStartScheduledOperationAsync(instanceId, scheduleId, dueAt, token)));
    }

    private static async Task<IResult> ScheduleAsync<T>(HttpContext http, IServiceProvider services,
        Func<IPostgreSqlBackupScheduleHost, CancellationToken, Task<T?>> run) where T : class
    {
        var hosts = services.GetRequiredService<ModuleHostSupervisor>();
        using var caller = hosts.AcquireAuthenticated(http.Request.Headers[ModuleHostProtocol.ServiceTokenHeader].ToString(),
            DatabaseObservationProtocol.ModuleId);
        if (caller is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
        var host = services.GetService<IPostgreSqlBackupScheduleHost>();
        if (host is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, caller.Stopping);
        try
        {
            var result = await run(host, stopping.Token);
            return result is null ? Results.NoContent() : Results.Json(result, RelaxKonOSJsonOptions.Default);
        }
        catch (Exception error) when (error is PostgreSqlBackupHostUnavailableException or IOException
            || error is OperationCanceledException && caller.Stopping.IsCancellationRequested && !http.RequestAborted.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> ReadAsync<TQueries, T>(HttpContext http, IServiceProvider services,
        Func<ModuleHostConnection, string, ClaimsPrincipal?> resolveUser,
        Func<TQueries, CancellationToken, Task<T>> read) where TQueries : class
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

        var queries = services.GetService<TQueries>();
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
