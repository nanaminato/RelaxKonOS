using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Startup.Modules;

namespace RelaxKonOS.Server.Startup;

internal sealed class InstalledProxyModule(InstalledModuleRecord record, string contentRoot)
    : ProxyModule, IInstalledModuleInfo
{
    private protected override ProxyConnectionExecutionGate Execution { get; } =
        new(TimeSpan.FromSeconds(30), () => ResolveEntry(record, contentRoot));
    private protected override ProxyExecutionCodeGate SubscriptionExecution { get; } =
        new("subscription", () => ResolveEntry(record, contentRoot));
    public string InstalledVersion => record.Version;
    public string ArtifactDigest => record.ArtifactSha256;
    public string SourceId => record.SourceId;
    public string TrustState => record.Quarantined ? "quarantined" : "installed";

    internal static bool MatchesManifest(ModulePackageManifest manifest) =>
        manifest.ModuleId == ServerModules.ProxyId && manifest.Purpose == "official" && manifest.Runtime == "in-process"
        && manifest.EntryType == ProxyConnectionExecutionGate.OverviewEntryType
        && manifest.EntryAssembly == $"server/{manifest.Rid}/{ProxyExecutionLoadContext.AssemblyFile}"
        && manifest.Capabilities.Length == 3
        && manifest.Capabilities.Contains(ServerCapabilities.Proxy, StringComparer.Ordinal)
        && manifest.Capabilities.Contains(ServerCapabilities.GatewayProxy, StringComparer.Ordinal)
        && manifest.Capabilities.Contains(ServerCapabilities.ProxyServer, StringComparer.Ordinal)
        && manifest.Dependencies.Length == 0;

    public override void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        if (record.Quarantined) return;
        using var verified = Execution.AcquireOverview();
        using var subscription = SubscriptionExecution.Acquire();
        _ = subscription.Create<IProxySubscriptionNormalizer>("RelaxKonOS.Server.Proxy.ProxySubscriptionNormalizer");
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
