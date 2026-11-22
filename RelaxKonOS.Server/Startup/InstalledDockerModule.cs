using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Docker;
using RelaxKonOS.Server.Startup.Modules;

namespace RelaxKonOS.Server.Startup;

internal sealed class InstalledDockerModule(InstalledModuleRecord record, string contentRoot)
    : DockerModule, IInstalledModuleInfo
{
    public override IReadOnlyList<string> Requires => record.Requires;
    private protected override DockerComposeExecution Execution { get; } =
        new(TimeSpan.FromSeconds(30), () => ResolveEntry(record, contentRoot));
    public string InstalledVersion => record.Version;
    public string ArtifactDigest => record.ArtifactSha256;
    public string SourceId => record.SourceId;
    public string TrustState => record.Quarantined ? "quarantined" : "installed";

    internal static bool MatchesManifest(ModulePackageManifest manifest) =>
        manifest.ModuleId == ServerModules.DockerId && manifest.Purpose == "official" && manifest.Runtime == "in-process"
        && manifest.EntryType == DockerComposeExecution.EntryType
        && manifest.EntryAssembly == $"server/{manifest.Rid}/{DockerComposeExecution.AssemblyFile}"
        && manifest.Capabilities is [ServerCapabilities.Docker];

    public override void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        if (record.Quarantined) return;
        _ = ResolveEntry(record, contentRoot);
        base.ConfigureServices(builder, context);
    }

    public override void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        if (!record.Quarantined) base.MapEndpoints(endpoints, context);
    }

    private static string ResolveEntry(InstalledModuleRecord record, string contentRoot)
    {
        var manifest = InstalledModuleStore.VerifyPackage(contentRoot, record);
        if (!MatchesManifest(manifest) || manifest.Rid != ModulePackagePlatform.CurrentRid)
            throw new ModulePackageException("module-package-installed-identity");
        return Path.Combine(InstalledModuleStore.ResolvePackageRoot(contentRoot, record.PackagePath), manifest.EntryAssembly);
    }
}
