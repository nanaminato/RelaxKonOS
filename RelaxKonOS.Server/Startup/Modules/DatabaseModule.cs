using RelaxKonOS.Protocol.Common;
using RelaxKonOS.ServerModule.Database;
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

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context) =>
        endpoints.MapDatabaseEndpoints();

    public async ValueTask<IReadOnlyList<ServerModuleOrphanedWork>> InspectOrphanedWorkAsync(
        IServiceProvider services, ServerModuleContext context, CancellationToken cancellationToken)
    {
        DatabaseQueryHost? host = null;
        DatabaseQueryHost QueryHost() => host ??= new(services.GetRequiredService<IPrivilegedOperationTransport>());

        var issues = await DatabaseDeactivationInspection.InspectAsync(
            context.Platform,
            () => (services.GetService<InstallationOperationStore>() ?? new InstallationOperationStore(context.Environment))
                .Read().Select(entry => entry.Operation),
            (instance, token) => QueryHost().ReadPostgreSqlAsync(instance, token),
            token => QueryHost().ReadMySqlAsync(token),
            cancellationToken);
        return issues.Select(issue => new ServerModuleOrphanedWork(Id, issue)).ToArray();
    }
}
