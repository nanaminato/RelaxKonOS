using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Installations;

using RelaxKonOS.ServerModule.Database.Contracts;

namespace RelaxKonOS.Server.Databases;

/// <summary>Only dispatches explicitly granted root-owned policies through the shared durable coordinator.
/// No credentials, native paths, privileged timers or direct database commands are owned by this worker.</summary>
public sealed class PostgreSqlBackupScheduler(IPostgreSqlBackupScheduleHost host,
    ILogger<PostgreSqlBackupScheduler> logger, TimeProvider? time = null) : BackgroundService
{
    private readonly TimeProvider _clock = time ?? TimeProvider.System;

    public async Task<InstallationOperationDto?> TickAsync(CancellationToken cancellationToken)
    {
        var slot = await host.ReadDueSlotAsync(cancellationToken);
        if (slot is null)
            return null;

        return await host.TryStartScheduledOperationAsync(slot.InstanceId, slot.ScheduleId, slot.DueAt, cancellationToken);
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
