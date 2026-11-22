using RelaxKonOS.Protocol.Databases;

using RelaxKonOS.ServerModule.Database.Contracts;

namespace RelaxKonOS.ServerModule.Database;

public sealed class DatabaseQueries(IDatabaseQueryHost host, PostgreSqlBackupScheduler scheduler) : IDatabaseQueries
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
