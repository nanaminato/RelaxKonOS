using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Server.Startup;

/// <summary>Metadata only. Package code is never loaded into the Server for this runtime.</summary>
internal sealed class InstalledIsolatedModule(InstalledModuleRecord record, string contentRoot)
    : IServerModule, IInstalledModuleInfo, IServerModuleHealthProbe
{
    private ModuleHostSupervisor? _supervisor;
    public string Id => record.ModuleId;
    public IReadOnlyList<string> Capabilities => [];
    public IReadOnlyList<string> Requires => record.Requires;
    public string InstalledVersion => record.Version;
    public string ArtifactDigest => record.ArtifactSha256;
    public string SourceId => record.SourceId;
    public string TrustState => record.Quarantined ? "quarantined" : "installed";

    public void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        if (!record.Quarantined) InstalledModuleStore.VerifyPackage(contentRoot, record);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context) =>
        _supervisor = endpoints.ServiceProvider.GetService<ModuleHostSupervisor>();

    public ValueTask<ServerModuleHealthModuleDto> CheckHealthAsync(bool enabled, CancellationToken cancellationToken)
    {
        var host = _supervisor?.Snapshot().SingleOrDefault(item => item.ModuleId == Id);
        var status = record.Quarantined ? "quarantined" : !enabled ? "disabled"
            : host?.Status == "running" ? "healthy" : host?.Status ?? "starting";
        return ValueTask.FromResult(new ServerModuleHealthModuleDto(Id, enabled, true, record.Version,
            record.SourceId, status, host?.ProblemCode));
    }
}
