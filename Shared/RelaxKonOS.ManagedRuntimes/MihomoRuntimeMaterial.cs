using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Security.Cryptography;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes;

/// <summary>Shape and bounds of local published-state material. Does not parse YAML or authorize paths.</summary>
public static class MihomoRuntimeMaterial
{
    public static string ConfigurationTunIntentIdentity(Guid operationId, RuntimeConfigurationPlan plan, MihomoTunNetworkIntent intent)
    {
        if (operationId == Guid.Empty || plan.RuntimeId != "mihomo" || plan.ActiveRevisionId == Guid.Empty
            || !IsDigest(plan.CandidateIdentity) || !IsDigest(intent.ManagementHash)
            || !ValidTunScope(intent.Device, intent.Address, intent.Destination, intent.DnsRoutingDomain)
            || !CanonicalIPv4(intent.ManagementAddress) || intent.Address == intent.ManagementAddress || intent.Destination == intent.ManagementAddress)
            throw new RuntimePolicyException("runtime.network_intent_invalid");
        return Convert.ToHexString(SHA256.HashData(RuntimeJson.Serialize(new
        {
            operationId, plan.InstallationId, plan.HostId, plan.ActiveRevisionId, plan.CandidateIdentity, intent
        })));
    }

    public static void RequireConfigurationTunIntent(Guid operationId, RuntimeConfigurationPlan plan,
        MihomoPublishedRuntime? candidate, MihomoTunNetworkIntent? intent)
    {
        if (intent is null && plan.NetworkIntentIdentity is null) return;
        if (intent is null || candidate is not { NetworkTransitionRequired: true }
            || Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(candidate.Yaml))) != plan.CandidateIdentity
            || plan.NetworkIntentIdentity != ConfigurationTunIntentIdentity(operationId, plan, intent))
            throw new RuntimePolicyException("runtime.authorization_changed");
        MihomoConfigurationPolicy.RequireDescriptorTun(candidate, intent.Device);
    }

    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal static bool ValidTunScope(string device, string address, string destination, string? dnsRoutingDomain) =>
        device is { Length: > 0 and < 16 } && char.IsAsciiLetterOrDigit(device[0])
        && device.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')
        && CanonicalIPv4(address) && CanonicalIPv4(destination) && address != destination
        && (dnsRoutingDomain is null || ValidDnsRoutingDomain(dnsRoutingDomain));

    public static bool ValidDnsRoutingDomain(string domain)
    {
        if (domain.Length > 253) return false;
        var labels = domain.Split('.');
        return labels.Length >= 2 && labels[^1] != "local"
            && labels.All(label => label.Length is >= 1 and <= 63 && label[0] != '-' && label[^1] != '-'
                && label.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'));
    }

    private static bool CanonicalIPv4(string value) => IPAddress.TryParse(value, out var address)
        && address.AddressFamily == AddressFamily.InterNetwork && address.ToString() == value
        && !IPAddress.IsLoopback(address) && address.GetAddressBytes()[0] is > 0 and < 224;

    public static string InstallationPlanIdentity(RuntimeChangePlan plan)
    {
        if (plan is null || plan.RuntimeId != "mihomo" || plan.Request is null || plan.Request.Change != RuntimeChange.Install || plan.Release is null)
            throw new RuntimePolicyException("runtime.request_invalid");
        return Convert.ToHexString(SHA256.HashData(RuntimeJson.Serialize(plan)));
    }
    public static void Validate(MihomoPublishedRuntime published)
    {
        if (published.Yaml is not { Length: > 0 and <= 4 * 1024 * 1024 } yaml
            || Encoding.UTF8.GetByteCount(yaml) > 4 * 1024 * 1024 || yaml.Contains('\0')
            || published.RequiredBuildTags is not { Count: <= 32 } tags
            || tags.Any(tag => tag is not { Length: > 0 and <= 64 } || !tag.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            || tags.Distinct(StringComparer.Ordinal).Count() != tags.Count
            || published.ControllerPort is < 1 or > 65535
            || published.ControllerSecret is not { Length: >= 16 and <= 512 } secret || secret.Any(char.IsControl)
            || published.Listeners is not { Count: > 0 and <= 128 } listeners
            || listeners.Any(listener => listener is null || listener.Port is < 1 or > 65535
                || listener.Address != "*" && !IPAddress.TryParse(listener.Address, out _))
            || listeners.Distinct().Count() != listeners.Count)
            throw new RuntimePolicyException("runtime.authorization_unavailable");
    }
}
