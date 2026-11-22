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

    public void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context) =>
        builder.Services.AddDatabaseServices();

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        endpoints.MapDatabaseCredentialEndpoints();
        endpoints.MapDatabaseObservationEndpoints();
    }

    public async ValueTask<IReadOnlyList<ServerModuleOrphanedWork>> InspectOrphanedWorkAsync(
        IServiceProvider services, ServerModuleContext context, CancellationToken cancellationToken)
    {
        DatabaseQueryHost? host = null;
        var issues = await BundledDatabaseExecution.Instance.InspectDeactivationAsync(
            context.Platform,
            () => (services.GetService<InstallationOperationStore>() ?? new InstallationOperationStore(context.Environment))
                .Read().Select(entry => entry.Operation),
            () => host ??= new(services.GetRequiredService<IPrivilegedOperationTransport>()),
            cancellationToken);
        return issues.Select(issue => new ServerModuleOrphanedWork(Id, issue)).ToArray();
    }
}
