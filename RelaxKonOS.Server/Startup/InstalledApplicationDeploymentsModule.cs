using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.ApplicationDeployments;
using RelaxKonOS.Server.Startup.Modules;

namespace RelaxKonOS.Server.Startup;

internal sealed class InstalledApplicationDeploymentsModule(InstalledModuleRecord record, string contentRoot)
    : ApplicationDeploymentsModule, IInstalledModuleInfo
{
    private protected override ApplicationArchiveExecution ArchiveExecution { get; } = new(() => ResolveEntry(record, contentRoot));
    public string InstalledVersion => record.Version;
    public string ArtifactDigest => record.ArtifactSha256;
    public string SourceId => record.SourceId;
    public string TrustState => record.Quarantined ? "quarantined" : "installed";

    internal static bool MatchesManifest(ModulePackageManifest manifest) =>
        manifest.ModuleId == ServerModules.ApplicationDeploymentsId && manifest.Purpose == "official" && manifest.Runtime == "in-process"
        && manifest.EntryType == ApplicationArchiveExecution.EntryType
        && manifest.EntryAssembly == $"server/{manifest.Rid}/{ApplicationArchiveExecution.AssemblyFile}"
        && manifest.Capabilities is [ServerCapabilities.ApplicationDeployments] && manifest.Dependencies is [];

    public override void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        if (record.Quarantined) return;
        ApplicationArchiveExecution.ValidateEntryPoint(ResolveEntry(record, contentRoot));
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
