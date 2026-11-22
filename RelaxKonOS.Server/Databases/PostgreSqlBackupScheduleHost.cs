using System.Text.Json;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Components;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.ServerModule.Database.Contracts;

namespace RelaxKonOS.Server.Databases;

public sealed class PostgreSqlBackupScheduleHost(IPrivilegedOperationTransport helper, InstallationCoordinator installations)
    : IPostgreSqlBackupScheduleHost
{
    private const string Actor = "system:postgresql-backup-schedule";

    public async Task<PostgreSqlState> ReadScheduleAsync(CancellationToken cancellationToken)
    {
        try
        {
            return PostgreSqlComponentAdapter.Decode(await helper.ExecuteAsync(
                new(PrivilegedOperationKind.PostgreSqlBackupScheduleQuery), cancellationToken));
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

    public InstallationOperationDto StartScheduledOperation(PostgreSqlInstallationRequest request, string key)
    {
        try
        {
            return installations.Start(InstallationServiceId.PostgreSqlBackupAutomation, InstallationOperationKind.Configure,
                JsonSerializer.SerializeToElement(request), Actor, key);
        }
        catch (InstallationException error)
        {
            throw new PostgreSqlBackupHostUnavailableException(error);
        }
    }
}
