using System.Net;
using System.Net.Sockets;

namespace RelaxKonOS.Protocol.Settings;

public sealed record HostNetworkAddress(string Address, int PrefixLength);
public sealed record HostNetworkAdapter(string Id, int Index, string Name, string Description, string Kind,
    bool Connected, long LinkSpeed, string MacAddress, bool Dhcp, bool AutomaticDns,
    IReadOnlyList<HostNetworkAddress> Addresses, IReadOnlyList<string> Gateways, IReadOnlyList<string> DnsServers,
    string Revision, bool CanConfigure, string? UnavailableReason = null);
public sealed record HostNetworkSnapshot(IReadOnlyList<HostNetworkAdapter> Adapters, string Platform);
public sealed record HostNetworkChange(string AdapterId, bool Dhcp, string? Address, int PrefixLength,
    string? Gateway, bool AutomaticDns, IReadOnlyList<string> DnsServers);
public sealed record HostNetworkApplyRequest(Guid OperationId, string ExpectedRevision, HostNetworkChange Change);
public sealed record HostNetworkApplyResult(Guid OperationId, DateTimeOffset ConfirmBefore);
public sealed record HostNetworkConfirmRequest(Guid OperationId);
public sealed record HostNetworkConfirmed(bool Confirmed);

public static class HostNetworkValidation
{
    public static bool IsIpv4(string? value) => IPAddress.TryParse(value, out var ip)
        && ip.AddressFamily == AddressFamily.InterNetwork && value == ip.ToString()
        && !IPAddress.IsLoopback(ip) && ip.GetAddressBytes()[0] is > 0 and < 224;

    public static bool IsValid(HostNetworkChange change) => change.AdapterId is { Length: > 0 and <= 128 }
        && !change.AdapterId.Any(char.IsControl)
        && (change.Dhcp || IsIpv4(change.Address) && change.PrefixLength is >= 1 and <= 32
            && (string.IsNullOrEmpty(change.Gateway) || IsIpv4(change.Gateway)))
        && change.DnsServers is { Count: <= 4 }
        && (change.AutomaticDns || change.DnsServers.Count > 0)
        && change.DnsServers.All(IsIpv4)
        && (change.Dhcp ? change.Address is null && change.Gateway is null : true)
        && (!change.AutomaticDns || change.DnsServers.Count == 0);
}
