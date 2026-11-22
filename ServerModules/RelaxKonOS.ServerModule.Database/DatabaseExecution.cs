using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.ServerModule.Database.Contracts;

namespace RelaxKonOS.ServerModule.Database;

public sealed class DatabaseExecution : IDatabaseExecution
{
    public void AddServices(IServiceCollection services)
    {
        services.AddSingleton<PostgreSqlBackupScheduler>();
        services.AddSingleton<IDatabaseQueries, DatabaseQueries>();
        services.AddHostedService(sp => sp.GetRequiredService<PostgreSqlBackupScheduler>());
    }

    public IDatabaseCredentialStore OpenCredentialStore(string root, IDataProtector protector) =>
        new DatabaseCredentialStore(root, protector);

    public ValueTask<IReadOnlyList<string>> InspectDeactivationAsync(HostPlatformKind platform,
        Func<IEnumerable<InstallationOperationDto>> readOperations, Func<IDatabaseQueryHost> queries,
        CancellationToken cancellationToken) => DatabaseDeactivationInspection.InspectAsync(
            platform, readOperations, (instance, token) => queries().ReadPostgreSqlAsync(instance, token),
            token => queries().ReadMySqlAsync(token), cancellationToken);
}
