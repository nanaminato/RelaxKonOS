using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Firewall;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup.Modules;

namespace RelaxKonOS.Server.Startup;

internal sealed class InstalledFirewallModule(InstalledModuleRecord record, string contentRoot)
    : FirewallModule, IInstalledModuleInfo
{
    public override IReadOnlyList<string> Requires => record.Requires;
    public override IServerModuleRuntimeFactory RuntimeFactory { get; } =
        new FirewallModuleRuntimeFactory(() => ResolveEntry(record, contentRoot));
    public string InstalledVersion => record.Version;
    public string ArtifactDigest => record.ArtifactSha256;
    public string SourceId => record.SourceId;
    public string TrustState => record.Quarantined ? "quarantined" : "installed";

    internal static bool MatchesManifest(ModulePackageManifest manifest) =>
        manifest.ModuleId == ServerModules.FirewallId && manifest.Purpose == "official" && manifest.Runtime == "in-process"
        && manifest.EntryType == CollectibleFirewallExecution.EntryType
        && manifest.EntryAssembly == $"server/{manifest.Rid}/{CollectibleFirewallExecution.AssemblyFile}"
        && manifest.Capabilities is [ServerCapabilities.Firewall];

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
