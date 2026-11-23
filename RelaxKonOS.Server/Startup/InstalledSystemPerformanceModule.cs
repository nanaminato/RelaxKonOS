using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Startup.Modules;
using RelaxKonOS.Server.SystemPerformance;

namespace RelaxKonOS.Server.Startup;

internal sealed class InstalledSystemPerformanceModule(InstalledModuleRecord record, string contentRoot)
    : SystemPerformanceModule, IInstalledModuleInfo
{
    public string InstalledVersion => record.Version;
    public string ArtifactDigest => record.ArtifactSha256;
    public string SourceId => record.SourceId;
    public string TrustState => record.Quarantined ? "quarantined" : "installed";

    internal static bool MatchesManifest(ModulePackageManifest manifest) =>
        manifest.ModuleId == ServerModules.SystemPerformanceId && manifest.Purpose == "official"
        && manifest.Runtime == "in-process" && manifest.Rid is "win-x64" or "win-arm64" or "linux-x64" or "linux-arm64"
        && manifest.EntryType == BundledPerformanceSource.EntryType(manifest.Rid.StartsWith("win-", StringComparison.Ordinal))
        && manifest.EntryAssembly == $"server/{manifest.Rid}/{BundledPerformanceSource.AssemblyFile}"
        && manifest.Capabilities is { Length: 2 } capabilities && capabilities.Contains(ServerCapabilities.Metrics)
        && capabilities.Contains(ServerCapabilities.Processes) && manifest.Dependencies is [];

    public override void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        if (!record.Quarantined) base.ConfigureServices(builder, context);
    }

    public override void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        if (!record.Quarantined) base.MapEndpoints(endpoints, context);
    }

    private protected override ISystemPerformanceSource CreateSource()
    {
        var manifest = InstalledModuleStore.VerifyPackage(contentRoot, record);
        if (!MatchesManifest(manifest) || manifest.Rid != ModulePackagePlatform.CurrentRid)
            throw new ModulePackageException("module-package-installed-identity");
        return BundledPerformanceSource.Create(Path.Combine(
            InstalledModuleStore.ResolvePackageRoot(contentRoot, record.PackagePath), manifest.EntryAssembly));
    }
}
