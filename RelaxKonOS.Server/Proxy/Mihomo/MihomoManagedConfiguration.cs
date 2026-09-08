using System.Net;
using System.Text;
using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>
/// Keeps the management endpoint and credential under Server ownership.  Profile YAML is
/// intentionally raw, but it must never be allowed to replace the loopback controller
/// settings that RelaxKonOS uses to manage a hosted Mihomo process.
/// </summary>
internal static class MihomoManagedConfiguration
{
    private static readonly HashSet<string> ControllerKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "external-controller",
        "secret",
    };
    private static readonly HashSet<string> SettingsKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "mixed-port", "allow-lan", "bind-address", "ipv6", "unified-delay", "log-level"
    };
    private static readonly HashSet<string> TunSettingsKeys = new(StringComparer.OrdinalIgnoreCase) { "tun" };
    private static readonly HashSet<string> GeoDataKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "geodata-mode", "geo-auto-update", "geo-update-interval", "geox-url"
    };

    public static string WithServerControllerSettings(string yaml, MihomoControllerOptions options, string secret)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(options);
        if (secret.Length is < 16 or > 512 || secret.Any(char.IsControl))
            throw new ArgumentException("The controller secret is invalid.", nameof(secret));

        var profileConfiguration = RemoveTopLevelControllerSettings(yaml);
        var builder = new StringBuilder(profileConfiguration.TrimEnd('\r', '\n'));
        if (builder.Length > 0) builder.Append('\n');
        builder.Append("external-controller: ").Append(ControllerAddress(options.Endpoint)).Append('\n');
        builder.Append("secret: \"").Append(EscapeYamlDoubleQuotedScalar(secret)).Append("\"\n");
        return builder.ToString();
    }

    public static string WithRuntimeSettings(string yaml, ProxySettingsDto settings)
    {
        var content = RemoveTopLevelSettings(yaml);
        content = ReplaceDnsEnabled(content, settings.DnsEnabled);
        var builder = new StringBuilder(content.TrimEnd('\r', '\n'));
        if (builder.Length > 0) builder.Append('\n');
        builder.Append("mixed-port: ").Append(settings.MixedPort.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("allow-lan: ").Append(settings.AllowLan ? "true" : "false").Append('\n');
        builder.Append("bind-address: ").Append(settings.AllowLan ? "\"*\"" : "\"" + settings.SystemProxyHost + "\"").Append('\n');
        builder.Append("ipv6: ").Append(settings.Ipv6Enabled ? "true" : "false").Append('\n');
        builder.Append("unified-delay: ").Append(settings.UnifiedDelay ? "true" : "false").Append('\n');
        builder.Append("log-level: ").Append(settings.LogLevel).Append('\n');
        return builder.ToString();
    }

    /// <summary>Replaces profile-provided TUN options with the bounded Server-owned subset.
    /// TUN is deliberately kept disabled here; the transaction service remains its only activation path.</summary>
    public static string WithManagedTunSettings(string yaml, ProxySettingsDto settings)
    {
        var tun = settings.Tun ?? ProxyTunSettingsDto.Default;
        var content = RemoveTopLevelKeys(yaml, TunSettingsKeys);
        var builder = new StringBuilder(content.TrimEnd('\r', '\n'));
        if (builder.Length > 0) builder.Append('\n');
        builder.Append("tun:\n");
        builder.Append("  enable: false\n");
        builder.Append("  stack: ").Append(tun.Stack).Append('\n');
        builder.Append("  device: \"").Append(EscapeYamlDoubleQuotedScalar(tun.DeviceName)).Append("\"\n");
        builder.Append("  auto-route: ").Append(tun.AutoRoute ? "true" : "false").Append('\n');
        builder.Append("  strict-route: ").Append(tun.StrictRoute ? "true" : "false").Append('\n');
        builder.Append("  auto-detect-interface: ").Append(tun.AutoDetectInterface ? "true" : "false").Append('\n');
        builder.Append("  dns-hijack:\n");
        builder.Append("    - \"").Append(EscapeYamlDoubleQuotedScalar(tun.DnsHijack)).Append("\"\n");
        builder.Append("  mtu: ").Append(tun.Mtu.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        return builder.ToString();
    }

    /// <summary>
    /// Managed Mihomo always uses the versioned GEO files staged in its <c>-d</c> directory.
    /// Removing profile-provided download settings prevents first-start networking from becoming
    /// a hidden dependency or allowing a subscription to replace the trusted data source.
    /// </summary>
    public static string WithServerGeoDataSettings(string yaml)
    {
        var content = RemoveTopLevelKeys(yaml, GeoDataKeys);
        var builder = new StringBuilder(content.TrimEnd('\r', '\n'));
        if (builder.Length > 0) builder.Append('\n');
        builder.Append("geodata-mode: false\n");
        builder.Append("geo-auto-update: false\n");
        return builder.ToString();
    }

    private static string RemoveTopLevelControllerSettings(string yaml)
    {
        using var reader = new StringReader(yaml);
        var retained = new StringBuilder(yaml.Length);
        var skipping = false;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (TryGetTopLevelKey(line, out var key))
                skipping = ControllerKeys.Contains(key);
            if (!skipping) retained.AppendLine(line);
        }
        return retained.ToString();
    }

    private static string RemoveTopLevelSettings(string yaml)
        => RemoveTopLevelKeys(yaml, SettingsKeys);

    private static string RemoveTopLevelKeys(string yaml, IReadOnlySet<string> keys)
    {
        using var reader = new StringReader(yaml);
        var retained = new StringBuilder(yaml.Length);
        var skipping = false;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (TryGetTopLevelKey(line, out var key)) skipping = keys.Contains(key);
            if (!skipping) retained.AppendLine(line);
        }
        return retained.ToString();
    }

    private static string ReplaceDnsEnabled(string yaml, bool enabled)
    {
        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var dnsIndex = lines.FindIndex(line => TryGetTopLevelKey(line, out var key) && key.Equals("dns", StringComparison.OrdinalIgnoreCase));
        if (dnsIndex < 0)
        {
            lines.Add("dns:"); lines.Add("  enable: " + (enabled ? "true" : "false"));
            return string.Join('\n', lines);
        }
        var end = dnsIndex + 1;
        while (end < lines.Count && (lines[end].Length == 0 || char.IsWhiteSpace(lines[end][0]) || lines[end].StartsWith('#'))) end++;
        for (var index = dnsIndex + 1; index < end; index++)
        {
            var trimmed = lines[index].TrimStart();
            if (trimmed.StartsWith("enable:", StringComparison.OrdinalIgnoreCase))
            {
                var indent = lines[index][..(lines[index].Length - trimmed.Length)];
                lines[index] = indent + "enable: " + (enabled ? "true" : "false");
                return string.Join('\n', lines);
            }
        }
        lines.Insert(dnsIndex + 1, "  enable: " + (enabled ? "true" : "false"));
        return string.Join('\n', lines);
    }

    private static bool TryGetTopLevelKey(string line, out string key)
    {
        key = "";
        if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] is '#' or '-') return false;
        var separator = line.IndexOf(':');
        if (separator <= 0) return false;
        var candidate = line[..separator].Trim();
        if (candidate.Length >= 2 && candidate[0] == candidate[^1] && candidate[0] is '\'' or '\"')
            candidate = candidate[1..^1];
        if (candidate.Length == 0 || candidate.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_')))
            return false;
        key = candidate;
        return true;
    }

    private static string ControllerAddress(Uri endpoint)
    {
        var host = endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? "127.0.0.1" : endpoint.Host;
        if (IPAddress.TryParse(host, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            host = "[" + host + "]";
        return host + ":" + endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string EscapeYamlDoubleQuotedScalar(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);
}
