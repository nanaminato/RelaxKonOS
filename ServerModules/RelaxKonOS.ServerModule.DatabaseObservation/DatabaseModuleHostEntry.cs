using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModuleHost.Sdk;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Databases;

namespace RelaxKonOS.ServerModule.DatabaseObservation;

/// <summary>The isolated database entry serves observation through the Core-owned read broker.</summary>
public sealed class DatabaseModuleHostEntry : IModuleHostEntry, IModuleHostHttp
{
    private CancellationTokenSource? _backupStopping;
    private Task? _backupWorker;

    public string Id => DatabaseObservationProtocol.ModuleId;

    public ValueTask StartAsync(IModuleHostContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var schedule = RequireDatabaseContext(context).DatabaseBackupSchedule;
        _backupStopping = new CancellationTokenSource();
        _backupWorker = RunBackupScheduleAsync(schedule, context.Logger, _backupStopping.Token);
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        using var stopping = Interlocked.Exchange(ref _backupStopping, null);
        if (stopping is null) return;
        await stopping.CancelAsync();
        await _backupWorker!;
    }

    private static async Task RunBackupScheduleAsync(IModuleDatabaseBackupSchedule schedule, ILogger logger,
        CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            do
            {
                try
                {
                    if (await schedule.ReadDueSlotAsync(stopping) is { } slot)
                        await schedule.SubmitAsync(slot, stopping);
                }
                catch (Exception error) when (error is HttpRequestException or IOException or JsonException
                    || error is OperationCanceledException && !stopping.IsCancellationRequested)
                {
                    logger.LogDebug("PostgreSQL backup schedule is waiting for Core ({FailureType})", error.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stopping));
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

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
            if (kind == DatabaseObservationKind.PostgreSqlState && response.IsSuccessStatusCode)
            {
                var snapshot = await response.Content.ReadFromJsonAsync<PostgreSqlObservationSnapshot>(
                    RelaxKonOSJsonOptions.Default, http.RequestAborted);
                if (snapshot?.State is null)
                    throw new JsonException("Missing PostgreSQL observation state.");
                await http.Response.WriteAsJsonAsync(snapshot.ToPublicState(), RelaxKonOSJsonOptions.Default, http.RequestAborted);
                return;
            }
            http.Response.StatusCode = (int)response.StatusCode;
            http.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or JsonException)
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
