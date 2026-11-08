using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>One exact IPv4 destination on an exclusively held, nonpersistent Mihomo TUN.
/// The archive transaction must freeze the plan and verify management reachability before using it.</summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxMihomoTunRoutes
{
    private readonly string address;
    private readonly string target;
    private readonly string management;

    internal LinuxMihomoTunRoutes(IPAddress address, IPAddress target, IPAddress management)
    {
        if (!IsUnicast(address) || !IsUnicast(target) || !IsUnicast(management)
            || address.Equals(target) || address.Equals(management) || target.Equals(management))
            throw new RuntimePolicyException("runtime.network_route_invalid");
        this.address = address.ToString();
        this.target = target.ToString();
        this.management = management.ToString();
    }

    private static bool IsUnicast(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork
        && address.GetAddressBytes()[0] is > 0 and < 224 && !IPAddress.IsLoopback(address);

    internal async Task ApplyAsync(LinuxMihomoTunDescriptor device)
    {
        // Verify absence before writing and use only exclusive adds. An existing route is never replaced.
        var link = await ReadLinkAsync(device.Name, device.InterfaceIndex);
        if (link.TryGetProperty("ifalias", out _) || (await ReadRouteAsync()).GetArrayLength() != 0
            || (await JsonAsync("-j", "-4", "address", "show", "to", address + "/32")).GetArrayLength() != 0)
            throw new RuntimePolicyException("runtime.network_route_conflict");
        await IpAsync("link", "set", "dev", device.Name, "alias", device.Owner.ToString("N"));
        await IpAsync("-4", "address", "add", address + "/32", "dev", device.Name, "noprefixroute");
        await IpAsync("link", "set", "dev", device.Name, "up");
        await IpAsync("-4", "route", "add", target + "/32", "dev", device.Name, "src", address, "scope", "link");
        await VerifyAsync(device.Owner, device.Name, device.InterfaceIndex);
    }

    internal async Task VerifyAsync(Guid owner, string device, uint index)
    {
        var link = await ReadLinkAsync(device, index);
        if (owner == Guid.Empty || !HasString(link, "ifalias", owner.ToString("N"))
            || !link.GetProperty("flags").EnumerateArray().Any(flag => flag.GetString() == "UP"))
            throw new RuntimePolicyException("runtime.network_device_identity_unknown");
        var routes = await ReadRouteAsync();
        if (routes.GetArrayLength() != 1 || !HasString(routes[0], "dst", target)
            || !HasString(routes[0], "dev", device) || !HasString(routes[0], "prefsrc", address)
            || !HasString(routes[0], "scope", "link") || routes[0].TryGetProperty("gateway", out _)
            || routes[0].TryGetProperty("metric", out _) || routes[0].TryGetProperty("nexthops", out _))
            throw new RuntimePolicyException("runtime.network_route_conflict");
        var addresses = await JsonAsync("-j", "-4", "address", "show", "dev", device);
        if (addresses.GetArrayLength() != 1 || !addresses[0].TryGetProperty("addr_info", out var entries)
            || entries.GetArrayLength() != 1 || !HasString(entries[0], "local", address)
            || !entries[0].TryGetProperty("prefixlen", out var prefix) || prefix.GetInt32() != 32)
            throw new RuntimePolicyException("runtime.network_route_conflict");
    }

    private static async Task<JsonElement> ReadLinkAsync(string device, uint index)
    {
        if (!LinuxMihomoTunDescriptor.ValidName(device) || index == 0)
            throw new RuntimePolicyException("runtime.network_device_invalid");
        var links = await JsonAsync("-j", "link", "show", "dev", device);
        if (links.GetArrayLength() != 1 || !HasString(links[0], "ifname", device)
            || !links[0].TryGetProperty("ifindex", out var actual) || actual.GetUInt32() != index)
            throw new RuntimePolicyException("runtime.network_device_identity_unknown");
        return links[0];
    }

    private Task<JsonElement> ReadRouteAsync() => JsonAsync("-j", "-4", "route", "show", "table", "main", "exact", target + "/32");

    internal async Task<string> ReadManagementHashAsync()
    {
        var snapshot = new[]
        {
            (await IpAsync("-4", "route", "get", management)).Trim(),
            (await IpAsync("-4", "rule", "show")).Trim(),
            (await IpAsync("-6", "rule", "show")).Trim()
        };
        return MihomoArchiveAdapter.Hash(JsonSerializer.SerializeToUtf8Bytes(snapshot));
    }

    internal async Task RequireManagementAsync(string expected)
    {
        if (await ReadManagementHashAsync() != expected)
            throw new RuntimePolicyException("runtime.management_route_changed");
    }

    internal async Task<bool> IsAbsentAsync(string device, uint index)
    {
        var links = await JsonAsync("-j", "link", "show");
        return !links.EnumerateArray().Any(link => HasString(link, "ifname", device)
                || link.TryGetProperty("ifindex", out var actual) && actual.GetUInt32() == index)
            && (await ReadRouteAsync()).GetArrayLength() == 0
            && (await JsonAsync("-j", "-4", "address", "show", "to", address + "/32")).GetArrayLength() == 0;
    }

    private static bool HasString(JsonElement value, string key, string expected) =>
        value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String && field.GetString() == expected;

    private static async Task<JsonElement> JsonAsync(params string[] arguments)
    {
        using var document = JsonDocument.Parse(await IpAsync(arguments));
        return document.RootElement.Clone();
    }

    private static Task<string> IpAsync(params string[] arguments) =>
        NativeRuntimeProcess.RunAsync("/usr/sbin/ip", arguments, 16384);
}
