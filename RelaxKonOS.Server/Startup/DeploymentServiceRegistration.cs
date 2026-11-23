using RelaxKonOS.Server.ApplicationDeployments;

namespace RelaxKonOS.Server.Startup;

internal static class DeploymentServiceRegistration
{
    public static void AddDeploymentServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<RelaxKonOS.Server.Installations.InstallationOperationStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Installations.InstallationCoordinator>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RelaxKonOS.Server.Installations.InstallationCoordinator>());
        builder.Services.AddSingleton<RelaxKonOS.Server.Installations.IInstallationService, RelaxKonOS.Server.Installations.GitInstallationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Installations.IInstallationService, RelaxKonOS.Server.Installations.TailscaleInstallationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Installations.IInstallationService, RelaxKonOS.Server.Installations.SmbInstallationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Installations.IInstallationService, RelaxKonOS.Server.Installations.NginxInstallationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Installations.IInstallationService, RelaxKonOS.Server.Installations.SquidInstallationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Installations.IInstallationService, RelaxKonOS.Server.Installations.FrpInstallationService>();
        // Mihomo installs only through the managed catalog adapter. The fixed-manifest installer is gone,
        // so no request shape can reach the retired download/archive path.
        builder.Services.AddSingleton<RelaxKonOS.Server.Installations.MihomoRuntimeInstallationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Installations.IInstallationService>(sp =>
            sp.GetRequiredService<RelaxKonOS.Server.Installations.MihomoRuntimeInstallationService>());
        builder.Services.AddSingleton<RelaxKonOS.Server.Installations.IInstallationService, RelaxKonOS.Server.Installations.SingBoxInstallationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Installations.IInstallationService, RelaxKonOS.Server.Installations.DockerInstallationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Installations.InstallationFileReferenceStore>();

        builder.Services.AddApplicationDeploymentServices();

        // Backup recovery has a separate operator-managed key domain. Its default is deliberately
        // unavailable, so no backup workflow can silently fall back to application Data Protection keys.
        builder.Services.AddSingleton(sp =>
            sp.GetRequiredService<IConfiguration>().GetSection("BackupRecovery")
                .Get<RelaxKonOS.Server.BackupRecovery.BackupRecoveryOptions>()
            ?? new RelaxKonOS.Server.BackupRecovery.BackupRecoveryOptions());
        builder.Services.AddSingleton<RelaxKonOS.Server.BackupRecovery.IBackupRecoveryKeyProvider,
            RelaxKonOS.Server.BackupRecovery.ConfigurationBackupRecoveryKeyProvider>();
        builder.Services.AddSingleton<RelaxKonOS.Server.BackupRecovery.BackupRecoveryObjectStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.BackupRecovery.BackupRecoveryManifestStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.BackupRecovery.ApplicationDefinitionBackupService>();
        builder.Services.AddHostedService<RelaxKonOS.Server.BackupRecovery.BackupRecoveryCoordinator>();
    }
}
