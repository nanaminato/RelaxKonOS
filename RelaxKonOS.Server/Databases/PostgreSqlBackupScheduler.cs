using System.Text.Json;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Components;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.Privileged;
namespace RelaxKonOS.Server.Databases;

/// <summary>Only dispatches explicitly granted root-owned policies through the shared durable coordinator.
/// No credentials, native paths, privileged timers or direct database commands are owned by this worker.</summary>
public sealed class PostgreSqlBackupScheduler(IPrivilegedOperationTransport helper,InstallationCoordinator installations,
    ILogger<PostgreSqlBackupScheduler> logger,TimeProvider? time=null):BackgroundService
{
    private const string Actor="system:postgresql-backup-schedule";
    private readonly TimeProvider clock=time??TimeProvider.System;
    public InstallationOperationDto? Observe(PostgreSqlState state)
    {
        if(state.BackupSchedule is not {} policy||state.InstanceId is not {} instance)return null;
        if(state.PendingMaintenance?.Action==PostgreSqlMaintenanceAction.ScheduledBackup&&state.PendingOperationId is {} pending)
            return installations.Get(pending)?.Operation;
        return installations.FindByKey(InstallationServiceId.PostgreSqlBackupAutomation,InstallationOperationKind.Configure,
            JsonSerializer.SerializeToElement(PostgreSqlBackupSchedulePolicy.Request(instance,policy)),Actor,PostgreSqlBackupSchedulePolicy.Key(policy))?.Operation
            ?? (policy.LastOperationId is {} id?installations.Get(id)?.Operation:null);
    }
    public async Task<InstallationOperationDto?> TickAsync(CancellationToken ct)
    {
        var state=PostgreSqlComponentAdapter.Decode(await helper.ExecuteAsync(new(PrivilegedOperationKind.PostgreSqlBackupScheduleQuery),ct));
        if(!state.Supported||state.PendingOperationId is not null||state.InstanceId is not {} instance||state.BackupSchedule is not {Enabled:true} policy||policy.NextDueAt>clock.GetUtcNow())return null;
        // Stable slot identity also covers loss of the response to coordinator acceptance. Existing failed or
        // interrupted tasks remain visible; a new tick never bypasses them with a new operation key.
        return installations.Start(InstallationServiceId.PostgreSqlBackupAutomation,InstallationOperationKind.Configure,
            JsonSerializer.SerializeToElement(PostgreSqlBackupSchedulePolicy.Request(instance,policy)),Actor,PostgreSqlBackupSchedulePolicy.Key(policy));
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try {await TickAsync(stoppingToken);}
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception ex) when(ex is ComponentLifecycleException or InstallationException or IOException)
            {logger.LogDebug("PostgreSQL backup schedule is waiting for verified host state ({FailureType})",ex.GetType().Name);}
            try {await Task.Delay(TimeSpan.FromMinutes(1),clock,stoppingToken);}
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
        }
    }
}
