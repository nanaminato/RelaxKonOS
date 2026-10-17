using System.Net;
using System.Net.Sockets;

namespace RelaxKonOS.ManagedRuntimes;

/// <summary>Local administrator grants for private mirrors, restricted to RFC1918/ULA networks and a port.</summary>
public sealed record RuntimePrivateNetwork(string Cidr, int Port);

public static class RuntimeNetworkPolicy
{
    private static readonly IPNetwork[] Private = Networks("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7");
    private static readonly IPNetwork[] Forbidden = Networks("0.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8",
        "169.254.0.0/16", "168.63.129.16/32", "192.0.0.0/24", "192.0.2.0/24", "192.88.99.0/24", "198.18.0.0/15", "198.51.100.0/24",
        "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4", "::/96", "64:ff9b::/96", "64:ff9b:1::/48",
        "100::/64", "2001::/23", "2001:db8::/32", "2002::/16", "fe80::/10", "fec0::/10", "ff00::/8");

    public static void ValidateGrant(RuntimePrivateNetwork grant)
    {
        if (grant is null || grant.Port is < 1 or > 65535 || !IPNetwork.TryParse(grant.Cidr, out var range)
            || !Private.Any(p => p.PrefixLength <= range.PrefixLength && p.Contains(range.BaseAddress)))
            throw new RuntimePolicyException("runtime.private_network_invalid");
    }

    public static bool AddressAllowed(IPAddress address, int port, IReadOnlyList<RuntimePrivateNetwork> grants)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0) return false;
        if (Forbidden.Any(n => n.Contains(address)) || IPAddress.IsLoopback(address)) return false;
        if (Private.Any(n => n.Contains(address))) return grants.Any(g => g.Port == port
            && IPNetwork.TryParse(g.Cidr, out var n) && n.Contains(address));
        // Unallocated IPv6 space is not accepted as public just because it is not on a deny list.
        return address.AddressFamily == AddressFamily.InterNetwork
            || address.AddressFamily == AddressFamily.InterNetworkV6 && IPNetwork.Parse("2000::/3").Contains(address);
    }

    public static void RequireAddresses(IEnumerable<IPAddress> addresses, int port, IReadOnlyList<RuntimePrivateNetwork> grants)
    {
        var resolved = addresses.ToArray();
        if (resolved.Length == 0 || resolved.Any(address => !AddressAllowed(address, port, grants)))
            throw new RuntimePolicyException("runtime.address_forbidden");
    }

    private static IPNetwork[] Networks(params string[] values) => values.Select(IPNetwork.Parse).ToArray();
}
