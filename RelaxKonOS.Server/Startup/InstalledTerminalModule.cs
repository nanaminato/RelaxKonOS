using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Startup.Modules;
using RelaxKonOS.Server.Terminal;
using RoyalTerminal.Terminal;

namespace RelaxKonOS.Server.Startup;

internal sealed class InstalledTerminalModule(InstalledModuleRecord record, string contentRoot)
    : TerminalModule, IInstalledModuleInfo
{
    public string InstalledVersion => record.Version;
    public string ArtifactDigest => record.ArtifactSha256;
    public string SourceId => record.SourceId;
    public string TrustState => record.Quarantined ? "quarantined" : "installed";

    internal static bool MatchesManifest(ModulePackageManifest manifest) =>
        manifest.ModuleId == ServerModules.TerminalId && manifest.Purpose == "official"
        && manifest.Runtime == "in-process" && manifest.Rid is "win-x64" or "win-arm64" or "linux-x64" or "linux-arm64"
        && manifest.EntryType == BundledTerminalPtyFactory.EntryType
        && manifest.EntryAssembly == $"server/{manifest.Rid}/{BundledTerminalPtyFactory.AssemblyFile}"
        && manifest.Capabilities is [ServerCapabilities.Terminal] && manifest.Dependencies is [];

    public override void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        if (!record.Quarantined) base.ConfigureServices(builder, context);
    }

    public override void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        if (!record.Quarantined) base.MapEndpoints(endpoints, context);
    }

    private protected override IPtyFactory CreateLocalFactory()
    {
        var manifest = InstalledModuleStore.VerifyPackage(contentRoot, record);
        if (!MatchesManifest(manifest) || manifest.Rid != ModulePackagePlatform.CurrentRid)
            throw new ModulePackageException("module-package-installed-identity");
        return BundledTerminalPtyFactory.Create(Path.Combine(
            InstalledModuleStore.ResolvePackageRoot(contentRoot, record.PackagePath), manifest.EntryAssembly));
    }
}
