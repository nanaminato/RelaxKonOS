namespace RelaxKonOS.Server.ApplicationDeployments;

internal static class ApplicationDeploymentRegistration
{
    public static void AddApplicationDeploymentServices(this IServiceCollection services)
    {
        // 容器化应用部署：定义（catalog）、机密、暂存、操作账本均为单例持久状态；部署服务串行化同一应用的
        // 变更，协调器是唯一调用方并把每个长操作变成可恢复的持久记录。
        services.AddSingleton(sp =>
            sp.GetRequiredService<IConfiguration>().GetSection("ApplicationDeployments")
                .Get<ApplicationDeploymentOptions>()
            ?? new ApplicationDeploymentOptions());
        services.AddSingleton<ApplicationDeploymentCatalogStore>();
        services.AddSingleton<ApplicationDeploymentSecretStore>();
        services.AddSingleton<ApplicationDeploymentStagingStore>();
        services.AddSingleton<ApplicationDeploymentOperationStore>();
        services.AddSingleton<ApplicationManagedComposeDispatch>();
        services.AddSingleton<ApplicationManagedComposeInstall>();
        services.AddSingleton<ApplicationDeploymentDefinitionMutationStore>();
        services.AddSingleton<ApplicationDeploymentImageTagCatalog>();
        services.AddSingleton<ApplicationDeploymentRuntime>();
        services.AddSingleton<IApplicationDeploymentProxyIntegration,
            ApplicationDeploymentProxyIntegration>();
        // The deployment domain only knows this port. The Git module registers the real
        // implementation later in the startup sequence, which replaces this unavailable default; a
        // disabled Git module therefore yields a stable "git build unavailable" problem instead of a
        // missing service or a silent fallback to another deployment source.
        services.AddSingleton<IDeploymentImageBuildSource,
            UnavailableDeploymentImageBuildSource>();
        services.AddSingleton<ApplicationDeploymentService>();
        services.AddSingleton<ApplicationDeploymentManager>();
        services.AddSingleton<ApplicationDeploymentCoordinator>();
        services.AddSingleton<ApplicationDeploymentLiveLogs>();
        services.AddSingleton<ApplicationDeploymentLogSubscriptions>();
        services.AddHostedService<ApplicationDeploymentLogBroadcastService>();
        services.AddHostedService(sp => sp.GetRequiredService<ApplicationDeploymentCoordinator>());
        // The readiness probe talks to the container's published loopback port, so it needs its own bounded
        // client whose timeout is a readiness timeout rather than a request timeout.
        services.AddHttpClient(ApplicationDeploymentRuntime.HealthClientName,
                client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
    }
}
