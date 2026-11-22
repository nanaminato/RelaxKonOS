using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Components;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.ServerModule.Database;

namespace RelaxKonOS.Server.Databases;

internal sealed class DatabaseQueryHost(IPrivilegedOperationTransport helper) : IDatabaseQueryHost
{
    public Task<PostgreSqlState> ReadPostgreSqlAsync(Guid? instance, CancellationToken cancellationToken) =>
        ReadPostgreSqlOperationAsync(new(PrivilegedOperationKind.PostgreSqlQuery, PostgreSqlInstance: instance), cancellationToken);

    public Task<PostgreSqlState> ReadPostgreSqlMaintenancePlanAsync(CancellationToken cancellationToken) =>
        ReadPostgreSqlOperationAsync(new(PrivilegedOperationKind.PostgreSqlMaintenancePlan), cancellationToken);

    public Task<PostgreSqlState> ReadPostgreSqlMigrationPlanAsync(CancellationToken cancellationToken) =>
        ReadPostgreSqlOperationAsync(new(PrivilegedOperationKind.PostgreSqlMigrationPlan), cancellationToken);

    public async Task<MySqlState> ReadMySqlAsync(CancellationToken cancellationToken) =>
        MySqlInstallationService.DecodeState(await helper.ExecuteAsync(new(PrivilegedOperationKind.MySqlQuery), cancellationToken));

    private async Task<PostgreSqlState> ReadPostgreSqlOperationAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken) =>
        PostgreSqlComponentAdapter.Decode(await helper.ExecuteAsync(request, cancellationToken));
}
