using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup.Modules;
using RelaxKonOS.Server.Tailscale;

namespace RelaxKonOS.Server.Startup;

internal sealed class InstalledTailscaleModule(InstalledModuleRecord record, string contentRoot)
    : TailscaleModule, IInstalledModuleInfo
{
    public override IReadOnlyList<string> Requires => record.Requires;
    public override IServerModuleRuntimeFactory RuntimeFactory { get; } =
        new TailscaleModuleRuntimeFactory(() => LoadExecution(record, contentRoot));
    public string InstalledVersion => record.Version;
    public string ArtifactDigest => record.ArtifactSha256;
    public string SourceId => record.SourceId;
    public string TrustState => record.Quarantined ? "quarantined" : "installed";

    internal static bool MatchesManifest(ModulePackageManifest manifest) =>
        manifest.ModuleId == ModuleId && manifest.Purpose == "official" && manifest.Runtime == "in-process"
        && manifest.EntryType == CollectibleTailscaleExecution.EntryType
        && manifest.EntryAssembly == $"server/{manifest.Rid}/{CollectibleTailscaleExecution.AssemblyFile}"
        && manifest.Capabilities is [ServerCapabilities.Tailscale];

    public override void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        if (record.Quarantined) return;
        using var execution = LoadExecution(record, contentRoot);
        base.ConfigureServices(builder, context);
    }

    public override void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        if (!record.Quarantined) base.MapEndpoints(endpoints, context);
    }

    private static CollectibleTailscaleExecution LoadExecution(InstalledModuleRecord record, string contentRoot)
    {
        if (record.Quarantined) throw new ModulePackageException("module-package-quarantined");
        var manifest = InstalledModuleStore.VerifyPackage(contentRoot, record);
        if (!MatchesManifest(manifest) || manifest.Rid != ModulePackagePlatform.CurrentRid)
            throw new ModulePackageException("module-package-installed-identity");
        var root = InstalledModuleStore.ResolvePackageRoot(contentRoot, record.PackagePath);
        return new CollectibleTailscaleExecution(Path.Combine(root, manifest.EntryAssembly));
    }
}
