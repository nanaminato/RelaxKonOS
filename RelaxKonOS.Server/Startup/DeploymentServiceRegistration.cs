namespace RelaxKonOS.Server.Startup;

internal static class DeploymentServiceRegistration
{
    public static void AddDeploymentServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<RelaxKonOS.Server.Installations.InstallationOperationStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Installations.InstallationCoordinator>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RelaxKonOS.Server.Installations.InstallationCoordinator>());
        builder.Services.AddSingleton<RelaxKonOS.Server.Installations.IInstallationService, RelaxKonOS.Server.Installations.GitInstallationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Installations.IInstallationService, RelaxKonOS.Server.Installations.SmbInstallationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Installations.IInstallationService, RelaxKonOS.Server.Installations.NginxInstallationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Installations.IInstallationService, RelaxKonOS.Server.Installations.FrpInstallationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Installations.IInstallationService, RelaxKonOS.Server.Installations.MihomoInstallationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Installations.IInstallationService, RelaxKonOS.Server.Installations.SingBoxInstallationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Installations.IInstallationService, RelaxKonOS.Server.Installations.DockerInstallationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Installations.InstallationFileReferenceStore>();

        // 容器化应用部署：定义（catalog）、机密、暂存、操作账本均为单例持久状态；部署服务串行化同一应用的
        // 变更，协调器是唯一调用方并把每个长操作变成可恢复的持久记录。
        builder.Services.AddSingleton(sp =>
            sp.GetRequiredService<IConfiguration>().GetSection("ApplicationDeployments")
                .Get<RelaxKonOS.Server.ApplicationDeployments.ApplicationDeploymentOptions>()
            ?? new RelaxKonOS.Server.ApplicationDeployments.ApplicationDeploymentOptions());
        builder.Services.AddSingleton<RelaxKonOS.Server.ApplicationDeployments.ApplicationDeploymentCatalogStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ApplicationDeployments.ApplicationDeploymentSecretStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ApplicationDeployments.ApplicationDeploymentStagingStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ApplicationDeployments.ApplicationDeploymentOperationStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ApplicationDeployments.ApplicationDeploymentDefinitionMutationStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ApplicationDeployments.ApplicationDeploymentImageTagCatalog>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ApplicationDeployments.ApplicationDeploymentRuntime>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ApplicationDeployments.IApplicationDeploymentProxyIntegration,
            RelaxKonOS.Server.ApplicationDeployments.ApplicationDeploymentProxyIntegration>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ApplicationDeployments.ApplicationDeploymentService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ApplicationDeployments.ApplicationDeploymentManager>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ApplicationDeployments.ApplicationDeploymentCoordinator>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ApplicationDeployments.ApplicationDeploymentLiveLogs>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ApplicationDeployments.ApplicationDeploymentLogSubscriptions>();
        builder.Services.AddHostedService<RelaxKonOS.Server.ApplicationDeployments.ApplicationDeploymentLogBroadcastService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RelaxKonOS.Server.ApplicationDeployments.ApplicationDeploymentCoordinator>());
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
        // The readiness probe talks to the container's published loopback port, so it needs its own bounded
        // client whose timeout is a readiness timeout rather than a request timeout.
        builder.Services.AddHttpClient(RelaxKonOS.Server.ApplicationDeployments.ApplicationDeploymentRuntime.HealthClientName,
                client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
    }
}
