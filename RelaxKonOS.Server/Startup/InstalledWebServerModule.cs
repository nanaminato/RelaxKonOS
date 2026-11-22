using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Startup.Modules;
using RelaxKonOS.Server.WebServer;

namespace RelaxKonOS.Server.Startup;

internal sealed class InstalledWebServerModule(InstalledModuleRecord record, string contentRoot)
    : WebServerModule, IInstalledModuleInfo
{
    private protected override WebServerExecutionCode Execution { get; } =
        new(TimeSpan.FromSeconds(30), () => ResolveEntry(record, contentRoot));
    public string InstalledVersion => record.Version;
    public string ArtifactDigest => record.ArtifactSha256;
    public string SourceId => record.SourceId;
    public string TrustState => record.Quarantined ? "quarantined" : "installed";

    internal static bool MatchesManifest(ModulePackageManifest manifest) =>
        manifest.ModuleId == ServerModules.WebServerId && manifest.Purpose == "official" && manifest.Runtime == "in-process"
        && manifest.EntryType == WebServerExecutionCode.EntryType
        && manifest.EntryAssembly == $"server/{manifest.Rid}/{WebServerExecutionCode.AssemblyFile}"
        && manifest.Capabilities is [ServerCapabilities.WebServer]
        && manifest.Dependencies is [{ ModuleId: ServerModules.CertificatesId }];

    public override void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        if (record.Quarantined) return;
        WebServerExecutionCode.ValidateEntryPoints(ResolveEntry(record, contentRoot));
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
