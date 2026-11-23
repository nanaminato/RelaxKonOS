using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Endpoints;
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
        if (_database is not null) builder.Services.AddDatabaseHostServices();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        _supervisor = endpoints.ServiceProvider.GetService<ModuleHostSupervisor>();
        if (_database is not null)
        {
            endpoints.MapDatabaseCredentialEndpoints();
            endpoints.MapDatabaseObservationRelay();
        }
    }

    public ValueTask<IReadOnlyList<ServerModuleOrphanedWork>> InspectOrphanedWorkAsync(
        IServiceProvider services, ServerModuleContext context, CancellationToken cancellationToken) =>
        _database?.InspectOrphanedWorkAsync(services, context, cancellationToken)
        ?? ValueTask.FromResult<IReadOnlyList<ServerModuleOrphanedWork>>([]);

    public ValueTask<ServerModuleHealthModuleDto> CheckHealthAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (_supervisor?.SnapshotInstalledHealth(Id, enabled) is { } supervised)
            return ValueTask.FromResult(supervised);

        var status = record.Quarantined ? "quarantined" : !enabled ? "disabled" : "starting";
        string? problem = null;
        if (status == "starting")
        {
            try
            {
                if (ModuleTrustStore.Read(contentRoot, record)?.Trusted != true)
                    problem = "module-host-code-trust-required";
            }
            catch (Exception error) when (error is RelaxKonOS.ModulePackaging.ModulePackageException
                or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                problem = "module-package-trust-invalid";
            }
            if (problem is not null) status = "blocked";
        }
        return ValueTask.FromResult(new ServerModuleHealthModuleDto(Id, enabled, true, null, record.SourceId, status, problem));
    }
}
