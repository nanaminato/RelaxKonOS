using RelaxKonOS.ServerModule.Database.Contracts;
using RelaxKonOS.Server.Components;
using RelaxKonOS.Server.Databases;
using RelaxKonOS.Server.Installations;

namespace RelaxKonOS.Server.Startup;

internal static class DatabaseServiceRegistration
{
    public static void AddDatabaseHostServices(this IServiceCollection services)
    {
        services.AddSingleton<PostgreSqlComponentAdapter>();
        services.AddSingleton<IComponentLifecycleAdapter>(services => services.GetRequiredService<PostgreSqlComponentAdapter>());
        services.AddSingleton<MySqlInstallationService>();
        services.AddSingleton<IInstallationService, MySqlRemoteInstallationService>();
        services.AddSingleton<IInstallationService, MySqlAdminRecoveryInstallationService>();
        services.AddSingleton<IInstallationService, MySqlAccountsInstallationService>();
        services.AddSingleton<IInstallationService>(sp =>
            sp.GetRequiredService<MySqlInstallationService>());
        services.AddSingleton<IInstallationService, PostgreSqlInstallationService>();
        services.AddSingleton<PostgreSqlCredentialStore>();
        services.AddSingleton<IInstallationService, PostgreSqlAccountsInstallationService>();
        services.AddSingleton<IInstallationService, PostgreSqlAdminRecoveryInstallationService>();
        services.AddSingleton<IInstallationService, PostgreSqlRemoteAccessInstallationService>();
        services.AddSingleton<IInstallationService, PostgreSqlMaintenanceInstallationService>();
        services.AddSingleton<IInstallationService, MySqlMaintenanceInstallationService>();
        services.AddSingleton<IInstallationService, PostgreSqlBackupAutomationInstallationService>();
        services.AddSingleton<PostgreSqlBackupScheduleHost>();
        services.AddSingleton<IPostgreSqlBackupScheduleHost>(sp => sp.GetRequiredService<PostgreSqlBackupScheduleHost>());
        services.AddSingleton<IPostgreSqlBackupLedger>(sp => sp.GetRequiredService<PostgreSqlBackupScheduleHost>());
        services.AddSingleton<IDatabaseQueryHost, DatabaseQueryHost>();
    }
}
