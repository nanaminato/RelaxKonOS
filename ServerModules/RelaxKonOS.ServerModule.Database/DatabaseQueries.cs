using RelaxKonOS.Protocol.Databases;

namespace RelaxKonOS.ServerModule.Database;

public interface IDatabaseQueryHost
{
    Task<PostgreSqlState> ReadPostgreSqlAsync(Guid? instance, CancellationToken cancellationToken);
    Task<PostgreSqlState> ReadPostgreSqlMaintenancePlanAsync(CancellationToken cancellationToken);
    Task<PostgreSqlState> ReadPostgreSqlMigrationPlanAsync(CancellationToken cancellationToken);
    Task<MySqlState> ReadMySqlAsync(CancellationToken cancellationToken);
}

public sealed class DatabaseQueries(IDatabaseQueryHost host, PostgreSqlBackupScheduler scheduler)
{
    public async Task<PostgreSqlState> ReadPostgreSqlAsync(Guid? instance, CancellationToken cancellationToken)
    {
        var state = await host.ReadPostgreSqlAsync(instance, cancellationToken);
        return state with { ScheduledOperation = scheduler.Observe(state) };
    }

    public Task<PostgreSqlState> ReadPostgreSqlMaintenancePlanAsync(CancellationToken cancellationToken) =>
        host.ReadPostgreSqlMaintenancePlanAsync(cancellationToken);

    public Task<PostgreSqlState> ReadPostgreSqlMigrationPlanAsync(CancellationToken cancellationToken) =>
        host.ReadPostgreSqlMigrationPlanAsync(cancellationToken);

    public Task<MySqlState> ReadMySqlAsync(CancellationToken cancellationToken) =>
        host.ReadMySqlAsync(cancellationToken);
}
