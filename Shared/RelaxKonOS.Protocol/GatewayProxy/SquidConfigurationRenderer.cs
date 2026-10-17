using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace RelaxKonOS.Protocol.GatewayProxy;

/// <summary>Shared by preview and the root Helper: privileged output is always rendered from validated data.</summary>
public static partial class SquidConfigurationRenderer
{
    public const string Header = "# Managed by RelaxKonOS Gateway Proxy v1";
    public static SquidConfigurationPreview Preview(SquidConfiguration? value)
    {
        var errors = new List<string>();
        if (value is null) return new(false, null, ["configuration.required"]);
        var bindValid = IPAddress.TryParse(value.BindAddress, out var bind) && !bind.IsIPv4MappedToIPv6 && bind.ScopeIdSafe() == 0;
        if (!bindValid) errors.Add("listener.address.invalid");
        if (value.Port is < 1024 or > 65535) errors.Add("listener.port.invalid");
        if (bindValid && !IPAddress.IsLoopback(bind!) && !value.AllowRemoteClients) errors.Add("listener.remote.opt_in_required");
        if (value.AllowedNetworks is not { Length: > 0 and <= 128 }) errors.Add("networks.required_or_too_many");
        else foreach (var source in value.AllowedNetworks)
        {
            if (source is null || !IPNetwork.TryParse(source, out var network) || network.PrefixLength == 0
                || network.BaseAddress.IsIPv4MappedToIPv6 || network.BaseAddress.ScopeIdSafe() != 0)
                errors.Add("network.invalid_or_unrestricted");
        }
        ValidateDomains(value.AllowedDomains, errors);
        ValidateDomains(value.DeniedDomains, errors);
        if (value.Upstream is { } upstream)
        {
            if (upstream.Port is < 1 or > 65535 || upstream.Port == value.Port) errors.Add("upstream.port.invalid_or_loop");
            if (!ValidHost(upstream.Host)) errors.Add("upstream.host.invalid");
        }
        if (errors.Count > 0) return new(false, null, errors.Distinct().ToArray());

        var text = new StringBuilder().AppendLine(Header);
        var address = bind!.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{bind}]" : bind.ToString();
        text.AppendLine($"http_port {address}:{value.Port}");
        text.AppendLine("visible_hostname relaxkonos-gateway");
        text.AppendLine("acl rk_sources src " + string.Join(' ', value.AllowedNetworks.Select(x => IPNetwork.Parse(x).ToString())));
        text.AppendLine("acl rk_safe_ports port 80 443");
        text.AppendLine("acl rk_tls_ports port 443");
        text.AppendLine("acl rk_connect method CONNECT");
        text.AppendLine("acl rk_local dst 0.0.0.0/8 127.0.0.0/8 169.254.0.0/16 224.0.0.0/4 ::/128 ::1/128 fe80::/10 ff00::/8");
        text.AppendLine("http_access deny manager");
        text.AppendLine("http_access deny !rk_safe_ports");
        text.AppendLine("http_access deny rk_connect !rk_tls_ports");
        text.AppendLine("http_access deny rk_local");
        if (value.DeniedDomains.Length > 0)
        {
            text.AppendLine("acl rk_denied dstdomain " + Domains(value.DeniedDomains));
            text.AppendLine("http_access deny rk_denied");
        }
        if (value.AllowedDomains.Length > 0)
        {
            text.AppendLine("acl rk_allowed dstdomain " + Domains(value.AllowedDomains));
            text.AppendLine("http_access allow rk_sources rk_allowed");
        }
        else text.AppendLine("http_access allow rk_sources");
        text.AppendLine("http_access deny all");
        text.AppendLine("cache deny all");
        text.AppendLine("access_log stdio:/var/log/squid/access.log squid");
        if (value.Upstream is { } parent)
        {
            var host = IPAddress.TryParse(parent.Host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]" : parent.Host.ToLowerInvariant();
            text.AppendLine($"cache_peer {host} parent {parent.Port} 0 no-query default name=rk_parent");
            text.AppendLine("never_direct allow all");
        }
        return new(true, text.ToString().Replace("\r\n", "\n"), []);
    }

    private static long ScopeIdSafe(this IPAddress address) => address.AddressFamily == AddressFamily.InterNetworkV6 ? address.ScopeId : 0;
    private static bool ValidHost(string? host) => host is { Length: > 0 and <= 253 }
        && (IPAddress.TryParse(host, out var ip) ? !ip.IsIPv4MappedToIPv6 && ip.ScopeIdSafe() == 0 : DomainPattern().IsMatch(host));
    private static void ValidateDomains(string[]? values, List<string> errors)
    {
        if (values is null || values.Length > 256) { errors.Add("domains.invalid_or_too_many"); return; }
        foreach (var domain in values)
            if (domain is null || domain.Length > 254 || !DomainPattern().IsMatch(domain.TrimStart('.')) || domain.StartsWith("..")
                || IPAddress.TryParse(domain.TrimStart('.'), out _)) errors.Add("domain.invalid");
    }
    private static string Domains(string[] values) => string.Join(' ', values.Select(x => x.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal));
    [GeneratedRegex(@"\A[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();
}
