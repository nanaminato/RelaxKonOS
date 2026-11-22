using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Server.Git;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup.Modules;

namespace RelaxKonOS.Server.Startup;

internal sealed class InstalledGitModule(InstalledModuleRecord record, string contentRoot)
    : GitModule, IInstalledModuleInfo
{
    public override IReadOnlyList<string> Requires => record.Requires;
    public override IServerModuleRuntimeFactory RuntimeFactory { get; } =
        new GitModuleRuntimeFactory(() => ResolveEntry(record, contentRoot));
    public string InstalledVersion => record.Version;
    public string ArtifactDigest => record.ArtifactSha256;
    public string SourceId => record.SourceId;
    public string TrustState => record.Quarantined ? "quarantined" : "installed";

    internal static bool MatchesManifest(ModulePackageManifest manifest) =>
        manifest.ModuleId == ServerModules.GitId && manifest.Purpose == "official" && manifest.Runtime == "in-process"
        && manifest.EntryType == CollectibleGitExecution.EntryType
        && manifest.EntryAssembly == $"server/{manifest.Rid}/{CollectibleGitExecution.AssemblyFile}"
        && manifest.Capabilities is [ServerCapabilities.Git]
        && manifest.CapabilityContractVersions?.GetValueOrDefault(ServerCapabilities.Git) == GitApiRoutes.CapabilityContractVersion;

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
        if (record.Quarantined) throw new ModulePackageException("module-package-quarantined");
        var manifest = InstalledModuleStore.VerifyPackage(contentRoot, record);
        if (!MatchesManifest(manifest) || manifest.Rid != ModulePackagePlatform.CurrentRid)
            throw new ModulePackageException("module-package-installed-identity");
        return Path.Combine(InstalledModuleStore.ResolvePackageRoot(contentRoot, record.PackagePath), manifest.EntryAssembly);
    }
}
