using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.ServerModule.Database.Contracts;

public interface IDatabaseQueryHost
{
    Task<PostgreSqlState> ReadPostgreSqlAsync(Guid? instance, CancellationToken cancellationToken);
    Task<PostgreSqlState> ReadPostgreSqlMaintenancePlanAsync(CancellationToken cancellationToken);
    Task<PostgreSqlState> ReadPostgreSqlMigrationPlanAsync(CancellationToken cancellationToken);
    Task<MySqlState> ReadMySqlAsync(CancellationToken cancellationToken);
}

public interface IDatabaseQueries
{
    Task<PostgreSqlState> ReadPostgreSqlAsync(Guid? instance, CancellationToken cancellationToken);
}

public interface IPostgreSqlBackupLedger
{
    InstallationOperationDto? ReadOperation(Guid operationId);
    InstallationOperationDto? FindScheduledOperation(PostgreSqlInstallationRequest request, string key);
}

public interface IPostgreSqlBackupScheduleHost
{
    Task<PostgreSqlBackupSlot?> ReadDueSlotAsync(CancellationToken cancellationToken);
    Task<InstallationOperationDto?> TryStartScheduledOperationAsync(Guid instance, Guid scheduleId,
        DateTimeOffset dueAt, CancellationToken cancellationToken);
}

public static class PostgreSqlObservation
{
    public static PostgreSqlObservationSnapshot Capture(PostgreSqlState state, IPostgreSqlBackupLedger ledger)
    {
        if (state.BackupSchedule is not { } policy || state.InstanceId is not { } instance)
            return new(state, null, null, null);
        if (state.PendingMaintenance?.Action == PostgreSqlMaintenanceAction.ScheduledBackup && state.PendingOperationId is { } pending)
            return new(state, ledger.ReadOperation(pending), null, null);

        var request = PostgreSqlBackupSchedulePolicy.Request(instance, policy);
        return new(state, null, ledger.FindScheduledOperation(request, PostgreSqlBackupSchedulePolicy.Key(policy)),
            policy.LastOperationId is { } last ? ledger.ReadOperation(last) : null);
    }
}

public sealed class PostgreSqlBackupHostUnavailableException(Exception cause)
    : Exception("The database backup host is unavailable.", cause);
