using RelaxKonOS.Protocol.Databases;

namespace RelaxKonOS.AppSDK;

public interface IServerPostgreSql
{
    Task<PostgreSqlState> ReadAsync(CancellationToken cancellationToken, Guid? instance = null);
    Task<PostgreSqlState> MaintenancePlanAsync(CancellationToken cancellationToken);
    Task<PostgreSqlState> MigrationPlanAsync(CancellationToken cancellationToken);
    Task StageCredentialAsync(PostgreSqlCredentialRequest request, CancellationToken cancellationToken);
}
