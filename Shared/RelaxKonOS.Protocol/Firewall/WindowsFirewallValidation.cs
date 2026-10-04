using System.Net;
using System.Net.Sockets;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Protocol.Firewall;

public static class WindowsFirewallValidation
{
    public static bool IsPolicy(FirewallDefaultPolicy? value) => value is FirewallDefaultPolicy.Allow or FirewallDefaultPolicy.Deny;
    public static bool IsRule(PrivilegedOperationRequest request) =>
        request.FirewallRuleAction is FirewallRuleAction.Allow or FirewallRuleAction.Deny
        && request.FirewallRuleDirection is FirewallRuleDirection.In or FirewallRuleDirection.Out
        && request.FirewallRuleProtocol is FirewallRuleProtocol.Tcp or FirewallRuleProtocol.Udp or FirewallRuleProtocol.Any
        && IsAddress(request.FirewallSource) && IsAddress(request.FirewallDestination)
        && IsPort(request.FirewallPort)
        && (request.FirewallRuleProtocol != FirewallRuleProtocol.Any || request.FirewallPort == "any");

    public static bool IsAddress(string? value)
    {
        if (value == "any") return true;
        if (value is null || value.Length > 64 || value.Contains('%')) return false;
        var parts = value.Split('/');
        return parts.Length <= 2 && IPAddress.TryParse(parts[0], out var address)
            && (parts.Length == 1 || int.TryParse(parts[1], out var bits) && bits >= 0
                && bits <= (address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128));
    }

    public static bool IsPort(string? value)
    {
        if (value == "any") return true;
        if (value is null || value.Length > 11) return false;
        var parts = value.Split(':');
        return parts.Length <= 2 && int.TryParse(parts[0], out var first) && first is > 0 and <= 65535
            && (parts.Length == 1 || int.TryParse(parts[1], out var last) && last >= first && last <= 65535);
    }
}
