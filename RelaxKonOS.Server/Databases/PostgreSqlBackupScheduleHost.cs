using System.Text.Json;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Components;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.ServerModule.Database.Contracts;

namespace RelaxKonOS.Server.Databases;

public sealed class PostgreSqlBackupScheduleHost(IPrivilegedOperationTransport helper, InstallationCoordinator installations,
    TimeProvider? time = null)
    : IPostgreSqlBackupScheduleHost, IPostgreSqlBackupLedger
{
    private const string Actor = "system:postgresql-backup-schedule";

    public async Task<PostgreSqlBackupSlot?> ReadDueSlotAsync(CancellationToken cancellationToken)
    {
        var due = await ReadDueScheduleAsync(cancellationToken);
        return due is { } current ? new(current.Instance, current.Schedule.Id, current.Schedule.NextDueAt) : null;
    }

    private async Task<(Guid Instance, PostgreSqlBackupSchedule Schedule)?> ReadDueScheduleAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var state = PostgreSqlComponentAdapter.Decode(await helper.ExecuteAsync(
                new(PrivilegedOperationKind.PostgreSqlBackupScheduleQuery), cancellationToken));
            if (!state.Supported || state.PendingOperationId is not null || state.InstanceId is not { } instance
                || state.BackupSchedule is not { Enabled: true } schedule
                || schedule.NextDueAt > (time ?? TimeProvider.System).GetUtcNow())
                return null;
            return (instance, schedule);
        }
        catch (ComponentLifecycleException error)
        {
            throw new PostgreSqlBackupHostUnavailableException(error);
        }
    }

    public InstallationOperationDto? ReadOperation(Guid operationId) => installations.Get(operationId)?.Operation;

    public InstallationOperationDto? FindScheduledOperation(PostgreSqlInstallationRequest request, string key) =>
        installations.FindByKey(InstallationServiceId.PostgreSqlBackupAutomation, InstallationOperationKind.Configure,
            JsonSerializer.SerializeToElement(request), Actor, key)?.Operation;

    public async Task<InstallationOperationDto?> TryStartScheduledOperationAsync(Guid instance, Guid scheduleId,
        DateTimeOffset dueAt, CancellationToken cancellationToken)
    {
        var due = await ReadDueScheduleAsync(cancellationToken);
        if (due is not { } current || current.Instance != instance
            || current.Schedule.Id != scheduleId || current.Schedule.NextDueAt != dueAt)
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Reuse the same slot identity even when acceptance succeeded but the response was lost.
            var request = PostgreSqlBackupSchedulePolicy.Request(instance, current.Schedule);
            var key = PostgreSqlBackupSchedulePolicy.Key(current.Schedule);
            return installations.Start(InstallationServiceId.PostgreSqlBackupAutomation, InstallationOperationKind.Configure,
                JsonSerializer.SerializeToElement(request), Actor, key);
        }
        catch (InstallationException error)
        {
            throw new PostgreSqlBackupHostUnavailableException(error);
        }
    }
}
