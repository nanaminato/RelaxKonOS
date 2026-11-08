using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace RelaxKonOS.ManagedRuntimes;

/// <summary>The same Linux policy lookup is used by the unprivileged Server and the root Helper.</summary>
public static class MihomoLinuxManagementBaseline
{
    public static async Task<string> ReadAsync(IPAddress address, uint userId,
        Func<string[], CancellationToken, Task<string?>> ip, CancellationToken ct)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new RuntimePolicyException("runtime.network_intent_invalid");
        var route = await ip(["-4", "route", "get", address.ToString(), "uid", userId.ToString(CultureInfo.InvariantCulture)], ct);
        var ipv4 = await ip(["-4", "rule", "show"], ct);
        var ipv6 = await ip(["-6", "rule", "show"], ct);
        if (string.IsNullOrWhiteSpace(route) || ipv4 is null || ipv6 is null)
            throw new RuntimePolicyException("runtime.management_route_unknown");
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new[] { route.Trim(), ipv4.Trim(), ipv6.Trim() })));
    }
}
