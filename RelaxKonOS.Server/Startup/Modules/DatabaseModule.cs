using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Components;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Startup.Modules;

internal sealed class DatabaseModule : IServerModule, IServerModuleDeactivationGuard
{
    public string Id => ServerModules.DatabaseId;
    public IReadOnlyList<string> Capabilities => [ServerCapabilities.Database];
    public IReadOnlyList<string> Requires => [];

    public void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context) =>
        builder.Services.AddDatabaseServices();

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context) =>
        endpoints.MapDatabaseEndpoints();

    public async ValueTask<IReadOnlyList<ServerModuleOrphanedWork>> InspectOrphanedWorkAsync(
        IServiceProvider services, ServerModuleContext context, CancellationToken cancellationToken)
    {
        var unresolved = new List<ServerModuleOrphanedWork>();
        try
        {
            var ledger = services.GetService<InstallationOperationStore>() ?? new InstallationOperationStore(context.Environment);
            foreach (var entry in ledger.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var operation = entry.Operation;
                if (IsDatabaseInstallation(operation.Service) && InstallationOperationStore.Active(operation))
                    unresolved.Add(new(Id, $"Installation operation {operation.OperationId} is {operation.State}."));
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            unresolved.Add(new(Id, $"The installation ledger cannot be read ({error.GetType().Name})."));
        }

        if (unresolved.Count != 0 || context.Platform != HostPlatformKind.Linux)
            return unresolved;

        try
        {
            var helper = services.GetRequiredService<IPrivilegedOperationTransport>();
            var postgres = new PostgreSqlComponentAdapter(helper);
            var primary = await postgres.ReadAsync(cancellationToken);
            InspectPostgreSql(primary, unresolved);
            foreach (var instance in primary.Instances ?? [])
            {
                if (instance.Id != primary.InstanceId)
                    InspectPostgreSql(await postgres.ReadAsync(cancellationToken, instance: instance.Id), unresolved);
            }

            var mysql = MySqlInstallationService.DecodeState(await helper.ExecuteAsync(
                new(PrivilegedOperationKind.MySqlQuery), cancellationToken));
            if (mysql.PendingOperationId is { } pending)
                unresolved.Add(new(Id, $"MySQL native operation {pending} requires recovery."));
            else if (mysql.ProblemCode is "database.state_unavailable" or "database.recovery_required")
                unresolved.Add(new(Id, "MySQL native responsibility could not be established."));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            unresolved.Add(new(Id, $"Native database responsibility could not be established ({error.GetType().Name})."));
        }
        return unresolved;
    }

    private void InspectPostgreSql(PostgreSqlState state, List<ServerModuleOrphanedWork> unresolved)
    {
        if (state.PendingOperationId is { } pending)
            unresolved.Add(new(Id, $"PostgreSQL native operation {pending} requires recovery."));
        else if (state.ProblemCode is "database.state_unavailable" or "database.instance_invalid" or "database.recovery_required")
            unresolved.Add(new(Id, "PostgreSQL native responsibility could not be established."));
        if (state.BackupSchedule is { Enabled: true })
            unresolved.Add(new(Id, $"PostgreSQL instance {state.InstanceId} still owns an enabled backup schedule."));
    }

    private static bool IsDatabaseInstallation(InstallationServiceId service) => service is
        InstallationServiceId.PostgreSql or InstallationServiceId.PostgreSqlAccounts
        or InstallationServiceId.PostgreSqlAdminRecovery or InstallationServiceId.PostgreSqlRemoteAccess
        or InstallationServiceId.PostgreSqlMaintenance or InstallationServiceId.PostgreSqlBackupAutomation
        or InstallationServiceId.MySql or InstallationServiceId.MySqlAccounts or InstallationServiceId.MySqlAdminRecovery
        or InstallationServiceId.MySqlRemoteAccess or InstallationServiceId.MySqlMaintenance;
}
