using System.Net.NetworkInformation;

namespace RelaxKonOS.Server.Proxy.Platform;

/// <summary>
/// Read-only host discovery used before any TUN action. Linux obtains the default route from
/// procfs; Windows remains fail-closed until the SCM helper supplies equivalent IP Helper data.
/// Apply/restore intentionally refuse until Goal 9's controlled-platform validation enables them.
/// </summary>
public sealed class HostProxyNetworkSafetyPlatform : IProxyNetworkSafetyPlatform
{
    public Task<ProxyManagementRouteSnapshot?> CaptureManagementRouteAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) return Task.FromResult<ProxyManagementRouteSnapshot?>(null);
        try
        {
            var route = File.ReadLines("/proc/net/route").Skip(1).Select(ParseRoute).FirstOrDefault(item => item is not null);
            if (route is null || !NetworkInterface.GetAllNetworkInterfaces().Any(item => item.Name == route.Interface && item.OperationalStatus == OperationalStatus.Up))
                return Task.FromResult<ProxyManagementRouteSnapshot?>(null);
            // System bypasses are invariant safety requirements, not user-editable proxy rules.
            IReadOnlyList<string> bypass = ["loopback", "remoteos-listeners", "active-management-session", "default-gateway", "lan", "ssh", "rdp"];
            return Task.FromResult<ProxyManagementRouteSnapshot?>(new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, true, route.Interface, route.Gateway, bypass));
        }
        catch (IOException) { return Task.FromResult<ProxyManagementRouteSnapshot?>(null); }
        catch (UnauthorizedAccessException) { return Task.FromResult<ProxyManagementRouteSnapshot?>(null); }
    }
    public Task<bool> ApplyTunAsync(ProxyManagementRouteSnapshot snapshot, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> VerifyManagementRouteAsync(ProxyManagementRouteSnapshot snapshot, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> RestoreAsync(ProxyManagementRouteSnapshot snapshot, CancellationToken cancellationToken) => Task.FromResult(false);
    private static Route? ParseRoute(string line)
    {
        var fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 3 || fields[1] != "00000000" || !uint.TryParse(fields[2], System.Globalization.NumberStyles.HexNumber, null, out var gateway)) return null;
        return new Route(fields[0], string.Join('.', BitConverter.GetBytes(gateway)));
    }
    private sealed record Route(string Interface, string Gateway);
}
