using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Residency;

namespace RelaxKonOS.Server.Startup;

/// <summary>Metadata only. Package code is never loaded into the Server for this runtime.</summary>
internal sealed class InstalledIsolatedModule(InstalledModuleRecord record, string contentRoot)
    : IServerModule, IInstalledModuleInfo, IServerModuleHealthProbe, IServerModuleWorkResidency, IServerModuleDeactivationGuard
{
    private readonly Modules.DatabaseModule? _database = record.ModuleId == ServerModules.DatabaseId ? new() : null;
    private ModuleHostSupervisor? _supervisor;
    public IServerModuleRuntimeFactory RuntimeFactory { get; } = new ResidentModuleWorkRuntimeFactory(record.ModuleId);
    public string Id => record.ModuleId;
    public IReadOnlyList<string> Capabilities => _database?.Capabilities ?? [];
    public IReadOnlyList<string> Requires => record.Requires;
    public string InstalledVersion => record.Version;
    public string ArtifactDigest => record.ArtifactSha256;
    public string SourceId => record.SourceId;
    public string TrustState => record.Quarantined ? "quarantined" : "installed";

    public void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        if (!record.Quarantined) InstalledModuleStore.VerifyPackage(contentRoot, record);
        _database?.ConfigureServices(builder, context);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        _supervisor = endpoints.ServiceProvider.GetService<ModuleHostSupervisor>();
        _database?.MapEndpoints(endpoints, context);
    }

    public ValueTask<IReadOnlyList<ServerModuleOrphanedWork>> InspectOrphanedWorkAsync(
        IServiceProvider services, ServerModuleContext context, CancellationToken cancellationToken) =>
        _database?.InspectOrphanedWorkAsync(services, context, cancellationToken)
        ?? ValueTask.FromResult<IReadOnlyList<ServerModuleOrphanedWork>>([]);

    public ValueTask<ServerModuleHealthModuleDto> CheckHealthAsync(bool enabled, CancellationToken cancellationToken)
    {
        var status = record.Quarantined ? "quarantined" : !enabled ? "disabled" : "starting";
        return ValueTask.FromResult(_supervisor?.SnapshotInstalledHealth(Id, enabled)
            ?? new ServerModuleHealthModuleDto(Id, enabled, true, null, record.SourceId, status, null));
    }
}
