using System.Net;
using System.Text;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.ManagedRuntimes;
using YamlDotNet.RepresentationModel;

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
        "external-controller", "external-controller-tls", "external-controller-unix", "external-controller-pipe",
        "external-ui", "external-ui-url", "external-ui-name", "external-ui-download-url",
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
    /// The activation flag is required on purpose: defaulting it to "disabled" turned every
    /// unrelated settings write into a silent TUN teardown while the durable marker kept claiming
    /// a live session.  A rewrite that is not itself a TUN transition must pass the already-live
    /// activation read by <see cref="ReadTunActivation"/>.</summary>
    public static string WithManagedTunSettings(string yaml, ProxySettingsDto settings, bool enabled,
        IReadOnlyList<string>? routeExclusions = null)
    {
        var tun = settings.Tun ?? ProxyTunSettingsDto.Default;
        var content = RemoveTopLevelKeys(yaml, TunSettingsKeys);
        var builder = new StringBuilder(content.TrimEnd('\r', '\n'));
        if (builder.Length > 0) builder.Append('\n');
        builder.Append("tun:\n");
        builder.Append("  enable: ").Append(enabled ? "true" : "false").Append('\n');
        builder.Append("  stack: ").Append(tun.Stack).Append('\n');
        builder.Append("  device: \"").Append(EscapeYamlDoubleQuotedScalar(tun.DeviceName)).Append("\"\n");
        builder.Append("  auto-route: ").Append(tun.AutoRoute ? "true" : "false").Append('\n');
        builder.Append("  strict-route: ").Append(tun.StrictRoute ? "true" : "false").Append('\n');
        builder.Append("  auto-detect-interface: ").Append(tun.AutoDetectInterface ? "true" : "false").Append('\n');
        builder.Append("  dns-hijack:\n");
        builder.Append("    - \"").Append(EscapeYamlDoubleQuotedScalar(tun.DnsHijack)).Append("\"\n");
        builder.Append("  mtu: ").Append(tun.Mtu.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        // These are an invariant safety boundary rather than profile preferences.  Mihomo owns
        // the route installation, while these exclusions keep the management network outside it.
        if (enabled && routeExclusions is { Count: > 0 })
        {
            builder.Append("  route-exclude-address:\n");
            foreach (var route in routeExclusions.Distinct(StringComparer.Ordinal))
                builder.Append("    - \"").Append(EscapeYamlDoubleQuotedScalar(route)).Append("\"\n");
        }
        return builder.ToString();
    }

    /// <summary>
    /// Reads the live TUN activation out of a managed configuration.  Activation belongs to the
    /// safety transaction rather than to the Manager settings written next to it, so any rewrite
    /// that is not itself a transition has to carry these two values forward.  The route
    /// exclusions are an invariant boundary, not a preference: keeping <c>enable: true</c> while
    /// dropping them would let auto-route swallow the management network.
    /// </summary>
    public static MihomoTunActivation ReadTunActivation(string yaml)
    {
        try { return ReadTunCore(yaml); }
        catch (RuntimePolicyException) { throw new ArgumentException("Invalid TUN configuration."); }
    }
    private static MihomoTunActivation ReadTunCore(string yaml)
    {
        var root = Parse(yaml);
        var node = MihomoYaml.Get(root, "tun");
        if (node is null) return new(false, []);
        if (node is not YamlMappingNode tun) throw new ArgumentException("Invalid TUN configuration.");
        var enable = MihomoYaml.Scalar(MihomoYaml.Get(tun, "enable"));
        if (enable is not null && !bool.TryParse(enable, out _)) throw new ArgumentException("Invalid TUN flag.");
        var excluded = MihomoYaml.Get(tun, "route-exclude-address");
        if (excluded is not null && excluded is not YamlSequenceNode) throw new ArgumentException("Invalid route exclusions.");
        var routes = excluded is YamlSequenceNode list
            ? list.Children.Select(child => MihomoYaml.Scalar(child) ?? throw new ArgumentException("Invalid route exclusion.")).ToArray()
            : [];
        return new(enable is not null && bool.Parse(enable), routes);
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

    private static string RemoveTopLevelControllerSettings(string yaml) => RemoveTopLevelKeys(yaml, ControllerKeys);
    private static string RemoveTopLevelSettings(string yaml) => RemoveTopLevelKeys(yaml, SettingsKeys);

    private static string RemoveTopLevelKeys(string yaml, IReadOnlySet<string> keys)
    {
        var root = Parse(yaml);
        MihomoYaml.Remove(root, keys);
        return MihomoYaml.Render(root);
    }

    private static string ReplaceDnsEnabled(string yaml, bool enabled)
    {
        try { return ReplaceDnsCore(yaml, enabled); }
        catch (RuntimePolicyException) { throw new ArgumentException("Invalid DNS configuration."); }
    }
    private static string ReplaceDnsCore(string yaml, bool enabled)
    {
        var root = Parse(yaml);
        var original = MihomoYaml.Get(root, "dns");
        var dns = original is null ? new YamlMappingNode() : original as YamlMappingNode
            ?? throw new ArgumentException("Invalid DNS configuration.");
        MihomoYaml.Remove(dns, ["enable"]);
        dns.Add("enable", enabled ? "true" : "false");
        MihomoYaml.Remove(root, ["dns"]);
        root.Add("dns", dns);
        return MihomoYaml.Render(root);
    }

    private static YamlMappingNode Parse(string yaml)
    {
        try { return MihomoYaml.Parse(yaml); }
        catch (RuntimePolicyException) { throw new ArgumentException("Invalid proxy profile YAML."); }
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

/// <summary>The TUN activation a managed configuration carries: the flag plus its safety boundary.</summary>
internal sealed record MihomoTunActivation(bool Enabled, IReadOnlyList<string> RouteExclusions);
