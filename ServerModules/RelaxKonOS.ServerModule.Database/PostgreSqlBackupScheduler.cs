using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.ServerModule.Database;

public interface IPostgreSqlBackupScheduleHost
{
    Task<PostgreSqlState> ReadScheduleAsync(CancellationToken cancellationToken);
    InstallationOperationDto? ReadOperation(Guid operationId);
    InstallationOperationDto? FindScheduledOperation(PostgreSqlInstallationRequest request, string key);
    InstallationOperationDto StartScheduledOperation(PostgreSqlInstallationRequest request, string key);
}

public sealed class PostgreSqlBackupHostUnavailableException(Exception cause) : Exception("The database backup host is unavailable.", cause);

/// <summary>Only dispatches explicitly granted root-owned policies through the shared durable coordinator.
/// No credentials, native paths, privileged timers or direct database commands are owned by this worker.</summary>
public sealed class PostgreSqlBackupScheduler(IPostgreSqlBackupScheduleHost host,
    ILogger<PostgreSqlBackupScheduler> logger, TimeProvider? time = null) : BackgroundService
{
    private readonly TimeProvider _clock = time ?? TimeProvider.System;

    public InstallationOperationDto? Observe(PostgreSqlState state)
    {
        if (state.BackupSchedule is not { } policy || state.InstanceId is not { } instance)
            return null;
        if (state.PendingMaintenance?.Action == PostgreSqlMaintenanceAction.ScheduledBackup && state.PendingOperationId is { } pending)
            return host.ReadOperation(pending);

        var request = PostgreSqlBackupSchedulePolicy.Request(instance, policy);
        return host.FindScheduledOperation(request, PostgreSqlBackupSchedulePolicy.Key(policy))
            ?? (policy.LastOperationId is { } id ? host.ReadOperation(id) : null);
    }

    public async Task<InstallationOperationDto?> TickAsync(CancellationToken cancellationToken)
    {
        var state = await host.ReadScheduleAsync(cancellationToken);
        if (!state.Supported || state.PendingOperationId is not null || state.InstanceId is not { } instance
            || state.BackupSchedule is not { Enabled: true } policy || policy.NextDueAt > _clock.GetUtcNow())
            return null;

        // Reuse the same slot identity even when acceptance succeeded but the response was lost.
        var request = PostgreSqlBackupSchedulePolicy.Request(instance, policy);
        return host.StartScheduledOperation(request, PostgreSqlBackupSchedulePolicy.Key(policy));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is PostgreSqlBackupHostUnavailableException or IOException)
            {
                logger.LogDebug("PostgreSQL backup schedule is waiting for verified host state ({FailureType})",
                    (error.InnerException ?? error).GetType().Name);
            }

            try { await Task.Delay(TimeSpan.FromMinutes(1), _clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
