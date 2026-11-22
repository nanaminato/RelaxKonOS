using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModuleHost.Sdk;
using RelaxKonOS.Protocol.Databases;

namespace RelaxKonOS.ServerModule.DatabaseObservation;

/// <summary>The isolated database entry serves observation through the Core-owned read broker.</summary>
public sealed class DatabaseModuleHostEntry : IModuleHostEntry, IModuleHostHttp
{
    public string Id => DatabaseObservationProtocol.ModuleId;

    public ValueTask StartAsync(IModuleHostContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = RequireDatabaseContext(context);
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public void MapEndpoints(IEndpointRouteBuilder endpoints, IModuleHostContext context)
    {
        var observation = RequireDatabaseContext(context).DatabaseObservation;
        endpoints.MapGet(PostgreSqlRoutes.State, (HttpContext http, Guid? instance) =>
            ReadAsync(http, observation, DatabaseObservationKind.PostgreSqlState, instance));
        endpoints.MapGet(PostgreSqlRoutes.MaintenancePlan, (HttpContext http) =>
            ReadAsync(http, observation, DatabaseObservationKind.PostgreSqlMaintenancePlan));
        endpoints.MapGet(PostgreSqlRoutes.MigrationPlan, (HttpContext http) =>
            ReadAsync(http, observation, DatabaseObservationKind.PostgreSqlMigrationPlan));
        endpoints.MapGet(MySqlRoutes.State, (HttpContext http) =>
            ReadAsync(http, observation, DatabaseObservationKind.MySqlState));
    }

    private IModuleHostDatabaseContext RequireDatabaseContext(IModuleHostContext context) =>
        context is IModuleHostDatabaseContext database && context.ModuleId == Id
            ? database : throw new InvalidOperationException("The database module requires a database observation host context.");

    private static async Task ReadAsync(HttpContext http, IModuleDatabaseObservation observation,
        DatabaseObservationKind kind, Guid? instance = null)
    {
        try
        {
            using var response = await observation.ReadAsync(kind, instance, http.RequestAborted);
            http.Response.StatusCode = (int)response.StatusCode;
            http.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            if (http.Response.HasStarted || http.RequestAborted.IsCancellationRequested)
            {
                http.Abort();
                return;
            }
            http.Response.Clear();
            http.Response.StatusCode = error is HttpRequestException { StatusCode: { } status }
                ? (int)status : StatusCodes.Status503ServiceUnavailable;
        }
    }
}
