using System.Buffers.Binary;
using System.Globalization;
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
    private readonly IPAddress management;
    private readonly uint managementUserId;
    private readonly string table;
    private const uint Priority = 11000;
    private const string Protocol = "186";
    private const string LinkScope = "253"; // RT_SCOPE_LINK, rendered numerically by ip -N.

    internal LinuxMihomoTunRoutes(IPAddress address, IPAddress target, IPAddress management, uint managementUserId, uint routingTable)
    {
        if (!IsUnicast(address) || !IsUnicast(target) || !IsUnicast(management)
            || address.Equals(target) || address.Equals(management) || target.Equals(management))
            throw new RuntimePolicyException("runtime.network_route_invalid");
        this.address = address.ToString();
        this.target = target.ToString();
        this.management = management;
        this.managementUserId = managementUserId;
        table = routingTable.ToString(CultureInfo.InvariantCulture);
    }

    internal static uint TableFor(Guid operation) =>
        1_000_000_000u + (BinaryPrimitives.ReadUInt32LittleEndian(operation.ToByteArray()) & 0x3fff_ffffu);

    private static bool IsUnicast(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork
        && address.GetAddressBytes()[0] is > 0 and < 224 && !IPAddress.IsLoopback(address);

    internal async Task ApplyAsync(LinuxMihomoTunDescriptor device)
    {
        // Verify absence before writing and use only exclusive adds. An existing route is never replaced.
        var link = await ReadLinkAsync(device.Name, device.InterfaceIndex);
        if (link.TryGetProperty("ifalias", out _) || (await ReadTableAsync()).Length != 0
            || (await JsonAsync("-j", "-4", "route", "show", "table", "main", "exact", target + "/32")).GetArrayLength() != 0
            || (await JsonAsync("-j", "-4", "address", "show", "to", address + "/32")).GetArrayLength() != 0)
            throw new RuntimePolicyException("runtime.network_route_conflict");
        await RequireRuleAsync(present: false);
        await IpAsync("link", "set", "dev", device.Name, "alias", device.Owner.ToString("N"));
        await IpAsync("-4", "address", "add", address + "/32", "dev", device.Name, "noprefixroute");
        await IpAsync("link", "set", "dev", device.Name, "up");
        await IpAsync("-4", "route", "add", "table", table, target + "/32", "dev", device.Name, "src", address, "scope", "link");
        await IpAsync(RuleCommand("add"));
        await VerifyAsync(device.Owner, device.Name, device.InterfaceIndex);
    }

    internal async Task VerifyAsync(Guid owner, string device, uint index)
    {
        var link = await ReadLinkAsync(device, index);
        if (owner == Guid.Empty || !HasString(link, "ifalias", owner.ToString("N"))
            || !link.GetProperty("flags").EnumerateArray().Any(flag => flag.GetString() == "UP"))
            throw new RuntimePolicyException("runtime.network_device_identity_unknown");
        await RequireRuleAsync(present: true);
        var routes = await ReadTableAsync();
        if (routes.Length != 1 || !HasString(routes[0], "dst", target)
            || !HasString(routes[0], "dev", device) || !HasString(routes[0], "prefsrc", address)
            || !HasString(routes[0], "scope", LinkScope) || routes[0].TryGetProperty("gateway", out _)
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

    private async Task<JsonElement[]> ReadTableAsync() =>
        (await JsonAsync("-j", "-N", "-4", "route", "show", "table", "all")).EnumerateArray()
            .Where(entry => HasString(entry, "table", table)).ToArray();

    internal async Task RequireManagementAsync(string expected, bool ownedRulePresent = false)
    {
        var measured = await MihomoLinuxManagementBaseline.ReadAsync(management, managementUserId,
            async (arguments, _) => await IpAsync(arguments), CancellationToken.None,
            ownedRulePresent ? rules => RequireRule(rules, present: true) : null);
        if (measured != expected)
            throw new RuntimePolicyException("runtime.management_route_changed");
    }

    internal async Task<bool> IsAbsentAsync(string device, uint index)
    {
        var links = await JsonAsync("-j", "link", "show");
        return !links.EnumerateArray().Any(link => HasString(link, "ifname", device)
                || link.TryGetProperty("ifindex", out var actual) && actual.GetUInt32() == index)
            && (await ReadTableAsync()).Length == 0
            && (await JsonAsync("-j", "-4", "address", "show", "to", address + "/32")).GetArrayLength() == 0;
    }

    internal async Task RequireRuleAsync(bool present) =>
        _ = RequireRule(await JsonAsync("-j", "-N", "-4", "rule", "show"), present);

    internal Task RemoveRuleAsync() => IpAsync(RuleCommand("del"));

    private string[] RuleCommand(string action) =>
        ["-4", "rule", action, "priority", Priority.ToString(CultureInfo.InvariantCulture), "to", target + "/32",
            "lookup", table, "protocol", Protocol];

    private JsonElement[] RequireRule(JsonElement rules, bool present)
    {
        var all = rules.EnumerateArray().ToArray();
        var scoped = all.Where(rule => HasPriority(rule) || HasString(rule, "table", table)).ToArray();
        if (!present && scoped.Length == 0) return all;
        if (present && scoped.Length == 1 && IsOwnedRule(scoped[0]))
            return all.Where(rule => !IsOwnedRule(rule)).ToArray();
        throw new RuntimePolicyException("runtime.network_rule_conflict");
    }

    private bool IsOwnedRule(JsonElement rule) => HasPriority(rule) && HasString(rule, "src", "all")
        && HasString(rule, "dst", target) && HasString(rule, "table", table) && HasString(rule, "protocol", Protocol)
        && rule.EnumerateObject().Count() == 5;

    private static bool HasPriority(JsonElement rule) => rule.TryGetProperty("priority", out var value)
        && value.TryGetUInt32(out var priority) && priority == Priority;

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
