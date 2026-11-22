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

public interface IDatabaseQueries : IDatabaseQueryHost;

public interface IPostgreSqlBackupScheduleHost
{
    Task<PostgreSqlState> ReadScheduleAsync(CancellationToken cancellationToken);
    InstallationOperationDto? ReadOperation(Guid operationId);
    InstallationOperationDto? FindScheduledOperation(PostgreSqlInstallationRequest request, string key);
    InstallationOperationDto StartScheduledOperation(PostgreSqlInstallationRequest request, string key);
}

public sealed class PostgreSqlBackupHostUnavailableException(Exception cause)
    : Exception("The database backup host is unavailable.", cause);
