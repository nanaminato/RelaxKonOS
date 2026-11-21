using RelaxKonOS.Protocol.Common;
using RelaxKonOS.ServerModule.Database;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Components;
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
        var issues = await DatabaseDeactivationInspection.InspectAsync(
            context.Platform,
            () => (services.GetService<InstallationOperationStore>() ?? new InstallationOperationStore(context.Environment))
                .Read().Select(entry => entry.Operation),
            (instance, token) => new PostgreSqlComponentAdapter(services.GetRequiredService<IPrivilegedOperationTransport>())
                .ReadAsync(token, instance: instance),
            async token => MySqlInstallationService.DecodeState(await services.GetRequiredService<IPrivilegedOperationTransport>()
                .ExecuteAsync(new(PrivilegedOperationKind.MySqlQuery), token)),
            cancellationToken);
        return issues.Select(issue => new ServerModuleOrphanedWork(Id, issue)).ToArray();
    }
}
