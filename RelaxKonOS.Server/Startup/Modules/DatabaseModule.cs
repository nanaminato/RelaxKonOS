using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Databases;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Startup.Modules;

internal sealed class DatabaseModule : IServerModule, IServerModuleDeactivationGuard
{
    public string Id => ServerModules.DatabaseId;
    public IReadOnlyList<string> Capabilities => [ServerCapabilities.Database];
    public IReadOnlyList<string> Requires => [];

    public void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        builder.Services.AddDatabaseHostServices();
        builder.Services.AddSingleton<PostgreSqlBackupScheduler>();
        builder.Services.AddHostedService(services => services.GetRequiredService<PostgreSqlBackupScheduler>());
        BundledDatabaseExecution.Instance.AddBundledObservation(builder.Services);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        endpoints.MapDatabaseCredentialEndpoints();
        endpoints.MapDatabaseObservationEndpoints();
    }

    public async ValueTask<IReadOnlyList<ServerModuleOrphanedWork>> InspectOrphanedWorkAsync(
        IServiceProvider services, ServerModuleContext context, CancellationToken cancellationToken)
    {
        var host = new Lazy<DatabaseQueryHost>(() => new(services.GetRequiredService<IPrivilegedOperationTransport>()));
        var issues = await DatabaseDeactivationInspection.InspectAsync(
            context.Platform,
            () => (services.GetService<InstallationOperationStore>() ?? new InstallationOperationStore(context.Environment))
                .Read().Select(entry => entry.Operation),
            (instance, token) => host.Value.ReadPostgreSqlAsync(instance, token),
            token => host.Value.ReadMySqlAsync(token),
            cancellationToken);
        return issues.Select(issue => new ServerModuleOrphanedWork(Id, issue)).ToArray();
    }
}
