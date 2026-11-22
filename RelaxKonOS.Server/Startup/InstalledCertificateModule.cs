using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Certificate;
using RelaxKonOS.Server.Startup.Modules;

namespace RelaxKonOS.Server.Startup;

internal sealed class InstalledCertificateModule(InstalledModuleRecord record, string contentRoot)
    : CertificateModule, IInstalledModuleInfo
{
    private protected override AcmeExecution Execution { get; } =
        new(TimeSpan.FromSeconds(30), () => ResolveEntry(record, contentRoot));
    public string InstalledVersion => record.Version;
    public string ArtifactDigest => record.ArtifactSha256;
    public string SourceId => record.SourceId;
    public string TrustState => record.Quarantined ? "quarantined" : "installed";

    internal static bool MatchesManifest(ModulePackageManifest manifest) =>
        manifest.ModuleId == ServerModules.CertificatesId && manifest.Purpose == "official" && manifest.Runtime == "in-process"
        && manifest.EntryType == AcmeExecution.EntryType
        && manifest.EntryAssembly == $"server/{manifest.Rid}/{AcmeExecution.AssemblyFile}"
        && manifest.Capabilities is [ServerCapabilities.Certificates] && manifest.Dependencies.Length == 0
        && AcmeExecution.Dependencies.All(name => manifest.Files.Any(file => file.Path == $"server/{manifest.Rid}/{name}.dll"));

    public override void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        if (record.Quarantined) return;
        AcmeExecution.ValidateEntryPoint(ResolveEntry(record, contentRoot));
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
