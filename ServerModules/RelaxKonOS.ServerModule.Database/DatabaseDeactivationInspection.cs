using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.ServerModule.Database;

public static class DatabaseDeactivationInspection
{
    public static async ValueTask<IReadOnlyList<string>> InspectAsync(
        HostPlatformKind platform, Func<IEnumerable<InstallationOperationDto>> readOperations,
        Func<Guid?, CancellationToken, Task<PostgreSqlState>> readPostgreSql,
        Func<CancellationToken, Task<MySqlState>> readMySql, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var unresolved = new List<string>();
        try
        {
            foreach (var operation in readOperations())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsDatabaseInstallation(operation.Service)
                    && operation.State is InstallationOperationState.Queued or InstallationOperationState.Running)
                    unresolved.Add($"Installation operation {operation.OperationId} is {operation.State}.");
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            unresolved.Add($"The installation ledger cannot be read ({error.GetType().Name}).");
        }

        if (unresolved.Count != 0 || platform != HostPlatformKind.Linux)
            return unresolved;

        try
        {
            var primary = await readPostgreSql(null, cancellationToken);
            InspectPostgreSql(primary, unresolved);
            foreach (var instance in primary.Instances ?? [])
            {
                if (instance.Id != primary.InstanceId)
                    InspectPostgreSql(await readPostgreSql(instance.Id, cancellationToken), unresolved);
            }

            var mysql = await readMySql(cancellationToken);
            if (mysql.PendingOperationId is { } pending)
                unresolved.Add($"MySQL native operation {pending} requires recovery.");
            else if (mysql.ProblemCode is "database.state_unavailable" or "database.recovery_required")
                unresolved.Add("MySQL native responsibility could not be established.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            unresolved.Add($"Native database responsibility could not be established ({error.GetType().Name}).");
        }
        return unresolved;
    }

    private static void InspectPostgreSql(PostgreSqlState state, List<string> unresolved)
    {
        if (state.PendingOperationId is { } pending)
            unresolved.Add($"PostgreSQL native operation {pending} requires recovery.");
        else if (state.ProblemCode is "database.state_unavailable" or "database.instance_invalid" or "database.recovery_required")
            unresolved.Add("PostgreSQL native responsibility could not be established.");
        if (state.BackupSchedule is { Enabled: true })
            unresolved.Add($"PostgreSQL instance {state.InstanceId} still owns an enabled backup schedule.");
    }

    private static bool IsDatabaseInstallation(InstallationServiceId service) => service is
        InstallationServiceId.PostgreSql or InstallationServiceId.PostgreSqlAccounts
        or InstallationServiceId.PostgreSqlAdminRecovery or InstallationServiceId.PostgreSqlRemoteAccess
        or InstallationServiceId.PostgreSqlMaintenance or InstallationServiceId.PostgreSqlBackupAutomation
        or InstallationServiceId.MySql or InstallationServiceId.MySqlAccounts or InstallationServiceId.MySqlAdminRecovery
        or InstallationServiceId.MySqlRemoteAccess or InstallationServiceId.MySqlMaintenance;
}
