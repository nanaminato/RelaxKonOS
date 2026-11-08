using System.Globalization;
using System.Net;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

internal sealed record MihomoTunDnsBaseline(string BusId, string Owner, string OtherSettingsHash);

/// <summary>Only the owned transient TUN link receives split DNS. The resolver removes its link state
/// when the kernel removes the device, so recovery observes disappearance without issuing a revert.</summary>
internal sealed class LinuxMihomoTunDns(string domain, IPAddress server, uint index)
{
    private const string Service = "org.freedesktop.resolve1";
    private const string Manager = "org.freedesktop.resolve1.Manager";
    private const string ManagerPath = "/org/freedesktop/resolve1";
    private readonly string linkIndex = index.ToString(CultureInfo.InvariantCulture);
    private readonly byte[] address = server.GetAddressBytes();

    internal static bool ValidBaseline(MihomoTunDnsBaseline baseline) =>
        Guid.TryParseExact(baseline.BusId, "N", out _) && ValidOwner(baseline.Owner)
        && baseline.OtherSettingsHash is { Length: 64 } && baseline.OtherSettingsHash.All(Uri.IsHexDigit);

    internal static async Task RequireAvailableAsync() => _ = await IdentityAsync();

    internal async Task<MihomoTunDnsBaseline> CaptureAsync()
    {
        var (bus, owner) = await IdentityAsync();
        var snapshot = await ReadSettingsAsync(owner);
        RequireLink(snapshot, present: false);
        if (snapshot.Domains.Any(row => Overlaps(row[1].GetString()!)))
            throw new RuntimePolicyException("runtime.network_dns_conflict");
        _ = await LinkPathAsync(owner);
        return new(bus, owner, HashOther(snapshot));
    }

    internal async Task ApplyAsync(MihomoTunDnsBaseline baseline)
    {
        await VerifyAsync(baseline, present: false);
        var owner = baseline.Owner;
        await CallAsync(owner, ManagerPath, Manager, "SetLinkDefaultRoute", "ib", linkIndex, "false");
        await CallAsync(owner, ManagerPath, Manager, "SetLinkDomains", "ia(sb)", linkIndex, "1", domain, "true");
        await CallAsync([owner, ManagerPath, Manager, "SetLinkDNS", "ia(iay)", linkIndex, "1", "2", "4",
            .. address.Select(value => value.ToString(CultureInfo.InvariantCulture))]);
        await VerifyAsync(baseline, present: true);
    }

    internal async Task VerifyAsync(MihomoTunDnsBaseline baseline, bool present)
    {
        if (await IdentityAsync() != (baseline.BusId, baseline.Owner))
            throw new RuntimePolicyException("runtime.network_dns_owner_unknown");
        var snapshot = await ReadSettingsAsync(baseline.Owner);
        RequireLink(snapshot, present);
        if (HashOther(snapshot) != baseline.OtherSettingsHash)
            throw new RuntimePolicyException("runtime.network_dns_conflict");
        if (present)
        {
            var path = await LinkPathAsync(baseline.Owner);
            var defaultRoute = await BusAsync("get-property", baseline.Owner, path, "org.freedesktop.resolve1.Link", "DefaultRoute");
            if (defaultRoute.ValueKind != JsonValueKind.False) throw new RuntimePolicyException("runtime.network_dns_conflict");
        }
    }

    private void RequireLink((JsonElement[] Dns, JsonElement[] Domains, JsonElement[] Fallback) snapshot, bool present)
    {
        var dns = snapshot.Dns.Where(OnLink).ToArray();
        var domains = snapshot.Domains.Where(OnLink).ToArray();
        if (!present && dns.Length == 0 && domains.Length == 0) return;
        if (present && dns.Length == 1 && domains.Length == 1 && dns[0][1].GetInt32() == 2
            && dns[0][2].EnumerateArray().Select(item => item.GetByte()).SequenceEqual(address)
            && domains[0][1].GetString() == domain && domains[0][2].GetBoolean()) return;
        throw new RuntimePolicyException("runtime.network_dns_conflict");
    }

    private string HashOther((JsonElement[] Dns, JsonElement[] Domains, JsonElement[] Fallback) snapshot) =>
        MihomoArchiveAdapter.Hash(JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            snapshot.Dns.Where(row => !OnLink(row)).ToArray(), snapshot.Domains.Where(row => !OnLink(row)).ToArray(), snapshot.Fallback
        }));

    private bool OnLink(JsonElement row) => row[0].GetUInt32() == index;

    private bool Overlaps(string other) => other != "." && (string.Equals(domain, other, StringComparison.OrdinalIgnoreCase)
        || domain.EndsWith("." + other, StringComparison.OrdinalIgnoreCase)
        || other.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    private static async Task<(JsonElement[] Dns, JsonElement[] Domains, JsonElement[] Fallback)> ReadSettingsAsync(string owner) =>
        (await PropertyAsync(owner, "DNS"), await PropertyAsync(owner, "Domains"), await PropertyAsync(owner, "FallbackDNS"));

    private static async Task<JsonElement[]> PropertyAsync(string owner, string property) =>
        (await BusAsync("get-property", owner, ManagerPath, Manager, property)).EnumerateArray().ToArray();

    private async Task<string> LinkPathAsync(string owner)
    {
        var result = await BusAsync("call", owner, ManagerPath, Manager, "GetLink", "i", linkIndex);
        return result[0].GetString() ?? throw new RuntimePolicyException("runtime.network_dns_owner_unknown");
    }

    private static async Task<(string, string)> IdentityAsync()
    {
        const string dbus = "org.freedesktop.DBus";
        const string path = "/org/freedesktop/DBus";
        var bus = (await BusAsync("call", dbus, path, dbus, "GetId"))[0].GetString();
        var owner = (await BusAsync("call", dbus, path, dbus, "GetNameOwner", "s", Service))[0].GetString();
        if (!Guid.TryParseExact(bus, "N", out _) || !ValidOwner(owner))
            throw new RuntimePolicyException("runtime.network_dns_owner_unknown");
        return (bus!, owner!);
    }

    private static bool ValidOwner(string? owner) => owner is { Length: >= 4 and <= 40 }
        && owner.StartsWith(":1.", StringComparison.Ordinal) && owner.AsSpan(3).IndexOfAnyExceptInRange('0', '9') == -1;

    private static Task<string> CallAsync(params string[] arguments) => RunAsync(["call", .. arguments]);

    private static async Task<JsonElement> BusAsync(params string[] arguments)
    {
        using var response = JsonDocument.Parse(await RunAsync(arguments));
        return response.RootElement.GetProperty("data").Clone();
    }

    private static Task<string> RunAsync(string[] arguments) =>
        NativeRuntimeProcess.RunAsync("/usr/bin/busctl", ["--system", "--timeout=3s", "--json=short", .. arguments], 16384);
}
