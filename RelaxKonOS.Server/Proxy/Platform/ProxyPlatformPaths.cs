using Microsoft.Extensions.Hosting;

namespace RelaxKonOS.Server.Proxy.Platform;

/// <summary>Fixed host-global locations. Callers can select an engine, never a filesystem path.</summary>
public sealed class ProxyPlatformPaths : IProxyPlatformPaths
{
    private readonly string _root = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RelaxKonOS", "Proxy")
        : "/var/lib/remoteos/proxy";

    public string GetEngineVersionsDirectory(string engineId) => Path.Combine(_root, "engines", ValidateEngine(engineId), "versions");
    public string GetEngineDataDirectory(string engineId) => Path.Combine(_root, "engines", ValidateEngine(engineId), "data");
    public string GetProtectedConfigurationDirectory() => OperatingSystem.IsWindows()
        ? Path.Combine(_root, "config")
        : "/etc/remoteos/proxy";
    public string GetStateDirectory() => Path.Combine(_root, "state");
    public string GetSanitizedLogDirectory() => OperatingSystem.IsWindows()
        ? Path.Combine(_root, "logs")
        : "/var/log/remoteos/proxy";

    private static string ValidateEngine(string engineId) => engineId == RelaxKonOS.Server.Proxy.Mihomo.MihomoEngine.Id
        ? engineId : throw new ArgumentOutOfRangeException(nameof(engineId), "Unknown proxy engine.");
}

/// <summary>Pure capability detection. It never changes firewall, Defender, routes, DNS, or services.</summary>
public sealed class ProxyPlatformService : IProxyPlatformService
{
    public Task<RelaxKonOS.Protocol.Proxy.ProxyPlatformCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var supported = OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
        var tunAvailable = OperatingSystem.IsWindows() || File.Exists("/dev/net/tun");
        return Task.FromResult(new RelaxKonOS.Protocol.Proxy.ProxyPlatformCapabilities(
            SupportsTun: supported && tunAvailable,
            SupportsAutoRoute: supported,
            SupportsAutoRedirect: false,
            SupportsDnsHijack: supported,
            SupportsNamedPipeController: OperatingSystem.IsWindows(),
            SupportsUnixSocketController: OperatingSystem.IsLinux(),
            ProblemCode: supported ? "" : RelaxKonOS.Protocol.Proxy.ProxyProblemCodes.PlatformCapabilityUnavailable));
    }
}
