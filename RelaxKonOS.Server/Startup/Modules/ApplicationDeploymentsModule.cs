using RelaxKonOS.Protocol.BackupRecovery;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.ApplicationDeployments;
using RelaxKonOS.Server.BackupRecovery;
using RelaxKonOS.Server.Endpoints;

namespace RelaxKonOS.Server.Startup.Modules;

internal sealed class ApplicationDeploymentsModule : IServerModule, IServerModuleDeactivationGuard
{
    public string Id => ServerModules.ApplicationDeploymentsId;
    public IReadOnlyList<string> Capabilities => [ServerCapabilities.ApplicationDeployments];
    public IReadOnlyList<string> Requires => [];

    public void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        builder.Services.AddApplicationDeploymentServices();
        builder.Services.AddSingleton<BackupRecoveryObjectStore>();
        builder.Services.AddSingleton<ApplicationDefinitionBackupService>();
        builder.Services.AddHostedService<BackupRecoveryCoordinator>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        endpoints.MapApplicationDeploymentEndpoints();
        endpoints.MapBackupRecoveryEndpoints();
        endpoints.MapHub<ApplicationDeploymentLogsHub>(RelaxKonOSEndpoints.ApplicationDeploymentLogsHubPath,
            options => options.CloseOnAuthenticationExpiration = true);
    }

    public ValueTask<IReadOnlyList<ServerModuleOrphanedWork>> InspectOrphanedWorkAsync(
        IServiceProvider services, ServerModuleContext context, CancellationToken cancellationToken)
    {
        try
        {
            var deployments = services.GetRequiredService<ApplicationDeploymentOperationStore>().Read()
                .Where(entry => ApplicationDeploymentOperationStore.BlocksMutation(entry.Operation))
                .Select(entry => new ServerModuleOrphanedWork(Id, $"Deployment operation {entry.Operation.OperationId} has unresolved work."));
            var backups = services.GetRequiredService<BackupRecoveryManifestStore>().Read()
                .Where(entry => entry.Manifest.State is BackupRecoveryOperationState.Queued or BackupRecoveryOperationState.Running)
                .Select(entry => new ServerModuleOrphanedWork(Id, $"Backup operation {entry.Manifest.BackupId} has unresolved work."));
            return ValueTask.FromResult<IReadOnlyList<ServerModuleOrphanedWork>>([.. deployments, .. backups]);
        }
        catch (Exception exception) when (exception is ApplicationDeploymentException or BackupRecoveryException)
        {
            return ValueTask.FromResult<IReadOnlyList<ServerModuleOrphanedWork>>([new(Id, "The deployment or backup ledger cannot be read.")]);
        }
    }
}
