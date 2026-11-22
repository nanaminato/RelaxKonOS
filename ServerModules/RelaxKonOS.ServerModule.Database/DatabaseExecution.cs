using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
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
}
