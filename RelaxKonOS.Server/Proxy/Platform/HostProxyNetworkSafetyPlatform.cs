using System.Net.NetworkInformation;

namespace RelaxKonOS.Server.Proxy.Platform;

/// <summary>
/// Host management-route guard used around the Mihomo-owned TUN transition.  Mihomo changes
/// routes and DNS after its managed configuration is reloaded; this boundary captures and
/// verifies the independent egress route before and after that transition.  It never accepts
/// caller-selected commands, addresses or interfaces.
/// </summary>
public sealed class HostProxyNetworkSafetyPlatform(ILogger<HostProxyNetworkSafetyPlatform>? logger = null) : IProxyNetworkSafetyPlatform
{
    public Task<ProxyManagementRouteSnapshot?> CaptureManagementRouteAsync(System.Net.IPAddress? managementAddress, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) return Task.FromResult(CaptureWindowsRoute(managementAddress));
        if (!OperatingSystem.IsLinux()) return Task.FromResult<ProxyManagementRouteSnapshot?>(null);
        try
        {
            var route = File.ReadLines("/proc/net/route").Skip(1).Select(ParseRoute).FirstOrDefault(item => item is not null);
            if (route is null || !NetworkInterface.GetAllNetworkInterfaces().Any(item => item.Name == route.Interface && item.OperationalStatus == OperationalStatus.Up))
                return Task.FromResult<ProxyManagementRouteSnapshot?>(null);
            // System bypasses are invariant safety requirements, not user-editable proxy rules.
            IReadOnlyList<string> bypass = ["loopback", "relaxkonos-listeners", "active-management-session", "default-gateway", "lan", "ssh", "rdp"];
            var addresses = ManagementAddresses(managementAddress);
            var snapshot = new ProxyManagementRouteSnapshot(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, true, route.Interface, route.Gateway, bypass, addresses);
            logger?.LogInformation("Captured Linux proxy management route. EgressInterface={EgressInterface} Gateway={Gateway} ManagementAddressCount={ManagementAddressCount}", route.Interface, route.Gateway, addresses.Count);
            return Task.FromResult<ProxyManagementRouteSnapshot?>(snapshot);
        }
        catch (IOException exception) { logger?.LogWarning(exception, "Could not capture the Linux proxy management route."); return Task.FromResult<ProxyManagementRouteSnapshot?>(null); }
        catch (UnauthorizedAccessException exception) { logger?.LogWarning(exception, "Access was denied while capturing the Linux proxy management route."); return Task.FromResult<ProxyManagementRouteSnapshot?>(null); }
    }
    public Task<bool> ApplyTunAsync(ProxyManagementRouteSnapshot snapshot, CancellationToken cancellationToken)
    {
        // The preceding managed Mihomo reload owns the actual TUN route/DNS mutation.  Do not
        // duplicate it with shell commands here; only accept a transition when its original
        // physical egress interface and gateway are still present.
        var available = HasTunCapability() && HasManagementRoute(snapshot);
        if (!available) logger?.LogWarning("Rejected TUN activation because the captured management route is no longer present. EgressInterface={EgressInterface}", snapshot.EgressInterface);
        return Task.FromResult(available);
    }
    public Task<bool> VerifyManagementRouteAsync(ProxyManagementRouteSnapshot snapshot, CancellationToken cancellationToken)
    {
        var reachable = HasManagementRoute(snapshot);
        if (!reachable) logger?.LogError("TUN transition removed the captured management route. EgressInterface={EgressInterface}", snapshot.EgressInterface);
        return Task.FromResult(reachable);
    }
    public Task<bool> RestoreAsync(ProxyManagementRouteSnapshot snapshot, CancellationToken cancellationToken)
    {
        var restored = HasManagementRoute(snapshot);
        if (!restored) logger?.LogError("TUN recovery could not verify the original management route. EgressInterface={EgressInterface}", snapshot.EgressInterface);
        return Task.FromResult(restored);
    }

    private ProxyManagementRouteSnapshot? CaptureWindowsRoute(System.Net.IPAddress? managementAddress)
    {
        try
        {
            var candidate = NetworkInterface.GetAllNetworkInterfaces()
                .Where(item => item.OperationalStatus == OperationalStatus.Up && item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(item => new { Interface = item, Gateway = item.GetIPProperties().GatewayAddresses.Select(address => address.Address).FirstOrDefault(address => !address.Equals(System.Net.IPAddress.Any) && !address.Equals(System.Net.IPAddress.IPv6Any)) })
                .FirstOrDefault(item => item.Gateway is not null);
            if (candidate is null) return null;
            IReadOnlyList<string> bypass = ["loopback", "relaxkonos-listeners", "active-management-session", "default-gateway", "lan", "ssh", "rdp"];
            var addresses = ManagementAddresses(managementAddress);
            logger?.LogInformation("Captured Windows proxy management route. EgressInterface={EgressInterface} Gateway={Gateway} ManagementAddressCount={ManagementAddressCount}", candidate.Interface.Name, candidate.Gateway, addresses.Count);
            return new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, true, candidate.Interface.Name, candidate.Gateway!.ToString(), bypass, addresses);
        }
        catch (NetworkInformationException exception) { logger?.LogWarning(exception, "Could not capture the Windows proxy management route."); return null; }
    }

    private static bool HasTunCapability() => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() && File.Exists("/dev/net/tun");
    private static IReadOnlyList<string> ManagementAddresses(System.Net.IPAddress? address)
        => address is null || System.Net.IPAddress.IsLoopback(address) ? [] : [address.ToString()];
    private static bool HasManagementRoute(ProxyManagementRouteSnapshot snapshot)
    {
        try
        {
            var network = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item => item.Name == snapshot.EgressInterface && item.OperationalStatus == OperationalStatus.Up);
            return network is not null && network.GetIPProperties().GatewayAddresses.Any(address => string.Equals(address.Address.ToString(), snapshot.DefaultGateway, StringComparison.OrdinalIgnoreCase));
        }
        catch (NetworkInformationException) { return false; }
    }
    private static Route? ParseRoute(string line)
    {
        var fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 3 || fields[1] != "00000000" || !uint.TryParse(fields[2], System.Globalization.NumberStyles.HexNumber, null, out var gateway)) return null;
        return new Route(fields[0], string.Join('.', BitConverter.GetBytes(gateway)));
    }
    private sealed record Route(string Interface, string Gateway);
}
