using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Startup.Modules;
using RelaxKonOS.Server.Tunnels;

namespace RelaxKonOS.Server.Startup;

internal sealed class InstalledTunnelsModule(InstalledModuleRecord record, string contentRoot)
    : TunnelsModule, IInstalledModuleInfo
{
    public string InstalledVersion => record.Version;
    public string ArtifactDigest => record.ArtifactSha256;
    public string SourceId => record.SourceId;
    public string TrustState => record.Quarantined ? "quarantined" : "installed";

    internal static bool MatchesManifest(ModulePackageManifest manifest) =>
        manifest.ModuleId == ModuleId && manifest.Purpose == "official" && manifest.Runtime == "in-process"
        && manifest.EntryType == CollectibleTunnelExecution.EntryType
        && manifest.EntryAssembly == $"server/{manifest.Rid}/{CollectibleTunnelExecution.AssemblyFile}"
        && manifest.Capabilities is [ServerCapabilities.Tunnels] && manifest.Dependencies is [];

    public override void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        if (record.Quarantined) return;
        using var execution = LoadExecution();
        base.ConfigureServices(builder, context);
    }

    public override void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        if (!record.Quarantined) base.MapEndpoints(endpoints, context);
    }

    private protected override CollectibleTunnelExecution LoadExecution()
    {
        var manifest = InstalledModuleStore.VerifyPackage(contentRoot, record);
        if (!MatchesManifest(manifest) || manifest.Rid != ModulePackagePlatform.CurrentRid)
            throw new ModulePackageException("module-package-installed-identity");
        return new(Path.Combine(InstalledModuleStore.ResolvePackageRoot(contentRoot, record.PackagePath), manifest.EntryAssembly));
    }
}
