using Microsoft.Extensions.DependencyInjection.Extensions;

namespace RelaxKonOS.Server.ApplicationDeployments;

internal static class ApplicationDeploymentRegistration
{
    public static void AddApplicationDeploymentState(this IServiceCollection services)
    {
        // Website publishing still reads application definitions and runtime state when deployment management is disabled.
        services.AddSingleton(sp =>
            sp.GetRequiredService<IConfiguration>().GetSection("ApplicationDeployments")
                .Get<ApplicationDeploymentOptions>()
            ?? new ApplicationDeploymentOptions());
        services.AddSingleton<ApplicationDeploymentOperationStore>();
        services.AddSingleton<ApplicationDeploymentCatalogStore>();
        services.AddSingleton<ApplicationContainerObservation>();
    }

    public static void AddApplicationDeploymentServices(this IServiceCollection services)
    {
        services.AddSingleton<ApplicationDeploymentRuntime>();
        services.AddHttpClient(ApplicationDeploymentRuntime.HealthClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        services.AddSingleton<ApplicationDeploymentSecretStore>();
        services.AddSingleton<ApplicationDeploymentImageTagCatalog>();
        services.AddSingleton<ApplicationDeploymentManager>();
        services.AddSingleton<ApplicationDeploymentStagingStore>();
        services.AddSingleton<ApplicationManagedComposeDispatch>();
        services.AddSingleton<ApplicationManagedComposeInstall>();
        services.AddSingleton<ApplicationDeploymentDefinitionMutationStore>();
        services.AddSingleton<IApplicationDeploymentProxyIntegration,
            ApplicationDeploymentProxyIntegration>();
        // Git supplies this port when enabled. The fallback keeps the domain usable without Git.
        services.TryAddSingleton<IDeploymentImageBuildSource,
            UnavailableDeploymentImageBuildSource>();
        services.AddSingleton<ApplicationDeploymentService>();
        services.AddSingleton<ApplicationDeploymentCoordinator>();
        services.AddSingleton<ApplicationDeploymentLiveLogs>();
        services.AddSingleton<ApplicationDeploymentLogSubscriptions>();
        services.AddHostedService<ApplicationDeploymentLogBroadcastService>();
        services.AddHostedService(sp => sp.GetRequiredService<ApplicationDeploymentCoordinator>());
    }
}
