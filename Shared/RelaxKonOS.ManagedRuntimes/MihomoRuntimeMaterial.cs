using System.Net;
using System.Text;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes;

/// <summary>Shape and bounds of local published-state material. Does not parse YAML or authorize paths.</summary>
public static class MihomoRuntimeMaterial
{
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
