using System.Net;
using System.Text.Json;

namespace RelaxKonOS.Protocol.ProxyServer;

/// <summary>
/// Shared, closed configuration generator. The same code renders the candidate inside the privilege
/// boundary and the sanitized preview shown to an administrator; no caller supplies native JSON.
/// </summary>
public static class SingBoxConfiguration
{
    public const int MaximumInbounds = 32;
    public const int MaximumCredentials = 1024;

    public static ProxyServerValidation Validate(ProxyServerConfiguration configuration)
    {
        if (configuration is null || configuration.Revision < 0 || configuration.Inbounds is null
            || configuration.Inbounds.Count > MaximumInbounds || !BaseUrl(configuration.SubscriptionBaseUrl))
            return new(false, [configuration is not null && !BaseUrl(configuration.SubscriptionBaseUrl)
                ? "proxy_server.subscription_base_invalid" : "proxy_server.config_invalid"]);

        var problems = new List<string>();
        var identities = new HashSet<Guid>();
        var ports = new HashSet<(int, bool)>();
        foreach (var inbound in configuration.Inbounds)
        {
            if (inbound is null || inbound.Id == Guid.Empty || !identities.Add(inbound.Id)
                || string.IsNullOrWhiteSpace(inbound.Name) || inbound.Name.Length > 128
                || !Enum.IsDefined(inbound.Protocol)
                || !IPAddress.TryParse(inbound.ListenAddress, out _)
                || inbound.ListenPort is < 1024 or > 65535
                || !ports.Add((inbound.ListenPort, ProxyServerPresets.IsUdp(inbound.Protocol)))
                || inbound.PublicPort is < 1 or > 65535 || !Host(inbound.PublicHost)
                || !Host(inbound.ServerName)
                || inbound.Protocol == ProxyServerProtocol.VlessReality && (!Host(inbound.HandshakeHost) || inbound.HandshakePort is < 1 or > 65535)
                || inbound.Protocol == ProxyServerProtocol.VlessReality && inbound.CertificateId is not null
                || ProxyServerPresets.RequiresCertificate(inbound.Protocol) && inbound.CertificateId is null
                || inbound.Protocol == ProxyServerProtocol.VlessWebSocket && !WebSocketPath(inbound.WebSocketPath))
            {
                problems.Add("proxy_server.config_invalid");
            }
            else if (inbound.Protocol == ProxyServerProtocol.VlessReality
                && IPAddress.TryParse(inbound.HandshakeHost, out var address) && !PublicAddress(address))
            {
                // A REALITY handshake target must never point at loopback, link-local, cloud metadata or management networks.
                problems.Add("proxy_server.handshake_address_forbidden");
            }
        }
        return new(problems.Count == 0, problems.Distinct().ToArray());
    }

    public static bool Host(string? value) => value is { Length: > 0 and <= 253 }
        && !value.Any(char.IsWhiteSpace) && value.IndexOfAny(['/', '\\', '@', '?', '#', '%']) < 0
        && Uri.CheckHostName(value) != UriHostNameType.Unknown
        && value is not ("0.0.0.0" or "::") && !value.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>The share host an administrator typed; never derived from a request's Host header.</summary>
    public static bool BaseUrl(string? value) => value is null || value is { Length: > 0 and <= 512 }
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    public static bool WebSocketPath(string? value) =>
        value is { Length: > 0 and <= 512 } && value.StartsWith('/') && !value.Any(char.IsControl);

    public static bool PublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal) return false;
        var b = address.GetAddressBytes();
        return b.Length == 4 ? !(b[0] is 0 or 10 or 127 || b[0] >= 224 || b[0] == 169 && b[1] == 254
            || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168
            || b[0] == 100 && b[1] is >= 64 and <= 127)
            : (b[0] & 0xe0) == 0x20;
    }

    /// <summary>Renders the candidate configuration loaded by the managed kernel.</summary>
    public static string Render(ProxyServerMaterial material, bool checkCertificateDates = true) =>
        Build(material, sanitize: false, checkCertificateDates);

    /// <summary>Renders the same shape with every secret removed; safe for logs and previews.</summary>
    public static string Projection(ProxyServerMaterial material) => Build(material, sanitize: true, checkCertificateDates: false);

    /// <summary>Renders a sanitized projection from the draft alone, without secret material.</summary>
    public static string Projection(ProxyServerConfiguration configuration)
    {
        var inbounds = configuration.Inbounds.Select(inbound => new Dictionary<string, object>
        {
            ["type"] = InboundType(inbound.Protocol),
            ["tag"] = inbound.Id.ToString("N"),
            ["listen"] = inbound.ListenAddress,
            ["listen_port"] = inbound.ListenPort,
            ["users"] = new[] { new { name = "(pending credentials)" } },
            ["tls"] = new Dictionary<string, object> { ["enabled"] = true, ["server_name"] = inbound.ServerName }
        }).ToArray();
        return Serialize(inbounds);
    }

    private static string Build(ProxyServerMaterial material, bool sanitize, bool checkCertificateDates)
    {
        if (material is null || material.Configuration is null || !Validate(material.Configuration).Valid
            || material.Credentials is null || material.Credentials.Count > MaximumCredentials
            || material.Credentials.Select(c => c.Id).Distinct().Count() != material.Credentials.Count
            || material.Keys is null
            || material.Keys.GroupBy(k => k.InboundId).Any(g => g.Count() != 1))
            throw new InvalidDataException("proxy_server.config_invalid");

        var inboundsById = material.Configuration.Inbounds.ToDictionary(i => i.Id);
        foreach (var secret in material.Credentials)
        {
            if (secret.Id == Guid.Empty || secret.Value is not { Length: >= 32 and <= 128 } || secret.InboundIds is not { Count: > 0 }
                || secret.InboundIds.Distinct().Count() != secret.InboundIds.Count
                || secret.InboundIds.Any(id => !inboundsById.ContainsKey(id))
                || !ProxyServerPresets.Shareable(secret.InboundIds.Select(id => inboundsById[id].Protocol).ToArray()))
                throw new InvalidDataException("proxy_server.credential_invalid");
        }

        var inbounds = new List<object>();
        foreach (var inbound in material.Configuration.Inbounds.Where(i => i.Enabled))
        {
            var users = material.Credentials.Where(c => c.InboundIds.Contains(inbound.Id)).ToArray();
            var vless = ProxyServerPresets.IsVless(inbound.Protocol);
            if (vless && users.Any(c => !Guid.TryParseExact(c.Value, "D", out _)))
                throw new InvalidDataException("proxy_server.credential_invalid");

            var tls = new Dictionary<string, object> { ["enabled"] = true, ["server_name"] = inbound.ServerName };
            if (inbound.Protocol == ProxyServerProtocol.VlessReality)
            {
                var key = material.Keys.SingleOrDefault(k => k.InboundId == inbound.Id) ?? throw new InvalidDataException("proxy_server.keys_missing");
                if (!Key(key.PrivateKey) || !Key(key.PublicKey) || key.ShortId.Length != 16 || !key.ShortId.All(Uri.IsHexDigit))
                    throw new InvalidDataException("proxy_server.keys_invalid");
                tls["reality"] = new
                {
                    enabled = true,
                    handshake = new { server = inbound.HandshakeHost, server_port = inbound.HandshakePort },
                    private_key = sanitize ? "***" : key.PrivateKey,
                    short_id = new[] { key.ShortId }
                };
            }
            else
            {
                var certificate = material.Certificates?.SingleOrDefault(c => c.InboundId == inbound.Id);
                if (sanitize)
                {
                    tls["certificate"] = new[] { "(certificate)" };
                    tls["key"] = new[] { "(private key)" };
                }
                else
                {
                    if (certificate is null) throw new InvalidDataException("proxy_server.certificate_invalid");
                    using var parsed = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(certificate.CertificatePem, certificate.PrivateKeyPem);
                    if (checkCertificateDates && (parsed.NotAfter.ToUniversalTime() <= DateTime.UtcNow || parsed.NotBefore.ToUniversalTime() > DateTime.UtcNow)
                        || !parsed.MatchesHostname(inbound.ServerName))
                        throw new InvalidDataException("proxy_server.certificate_invalid");
                    tls["certificate"] = new[] { certificate.CertificatePem };
                    tls["key"] = new[] { certificate.PrivateKeyPem };
                }
            }

            var item = new Dictionary<string, object>
            {
                ["type"] = InboundType(inbound.Protocol),
                ["tag"] = inbound.Id.ToString("N"),
                ["listen"] = inbound.ListenAddress,
                ["listen_port"] = inbound.ListenPort,
                ["users"] = users.Select(c => User(c, inbound.Protocol, sanitize)).ToArray(),
                ["tls"] = tls
            };
            if (inbound.Protocol == ProxyServerProtocol.VlessWebSocket)
                item["transport"] = new { type = "ws", path = inbound.WebSocketPath };
            inbounds.Add(item);
        }
        return Serialize(inbounds);
    }

    private static object User(ProxyServerSecret secret, ProxyServerProtocol protocol, bool sanitize)
    {
        var name = secret.Id.ToString("N");
        if (!ProxyServerPresets.IsVless(protocol)) return new { name, password = sanitize ? "***" : secret.Value };
        var flow = protocol == ProxyServerProtocol.VlessReality ? "xtls-rprx-vision" : "";
        return new { name, uuid = sanitize ? "***" : secret.Value, flow };
    }

    private static string InboundType(ProxyServerProtocol protocol) => protocol switch
    {
        ProxyServerProtocol.Trojan => "trojan",
        ProxyServerProtocol.Hysteria2 => "hysteria2",
        _ => "vless"
    };

    private static string Serialize(IReadOnlyList<object> inbounds) => JsonSerializer.Serialize(new
    {
        log = new { level = "error", timestamp = true },
        dns = new { servers = new[] { new { type = "local", tag = "local" } } },
        inbounds,
        outbounds = new[] { new { type = "direct", tag = "direct" } },
        route = new
        {
            default_domain_resolver = "local",
            rules = new object[] {
                new { action = "resolve" },
                new { ip_is_private = true, action = "reject" },
                new { ip_cidr = new[] { "0.0.0.0/8", "100.64.0.0/10", "169.254.0.0/16", "224.0.0.0/4", "::/128", "ff00::/8" }, action = "reject" }
            },
            final = "direct"
        }
    }, new JsonSerializerOptions { WriteIndented = true });

    public static bool Key(string value) => value.Length == 43 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
