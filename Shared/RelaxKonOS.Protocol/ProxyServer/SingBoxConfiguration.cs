using System.Net;
using System.Text.Json;

namespace RelaxKonOS.Protocol.ProxyServer;

/// <summary>Shared closed configuration generator, also validated inside the privilege boundary.</summary>
public static class SingBoxConfiguration
{
    public static ProxyServerValidation Validate(ProxyServerConfiguration configuration)
    {
        var problems = new List<string>();
        if (configuration.Revision < 0 || configuration.Inbounds is null || configuration.Inbounds.Count > 32)
            return new(false, ["proxy_server.config_invalid"]);
        var identities = new HashSet<Guid>();
        var ports = new HashSet<(int, bool)>();
        foreach (var inbound in configuration.Inbounds)
        {
            if (inbound is null || inbound.Id == Guid.Empty || !identities.Add(inbound.Id)
                || string.IsNullOrWhiteSpace(inbound.Name) || inbound.Name.Length > 128
                || !Enum.IsDefined(inbound.Protocol)
                || !IPAddress.TryParse(inbound.ListenAddress, out _)
                || inbound.ListenPort is < 1024 or > 65535 || !ports.Add((inbound.ListenPort, inbound.Protocol == ProxyServerProtocol.Hysteria2))
                || inbound.PublicPort is < 1 or > 65535 || !Host(inbound.PublicHost)
                || !Host(inbound.ServerName)
                || inbound.Protocol == ProxyServerProtocol.VlessReality && (!Host(inbound.HandshakeHost) || inbound.HandshakePort is < 1 or > 65535)
                || inbound.Protocol != ProxyServerProtocol.VlessReality && inbound.CertificateId is null
                || inbound.Protocol == ProxyServerProtocol.VlessWebSocket && (inbound.WebSocketPath is not { Length: > 0 and <= 512 }
                    || !inbound.WebSocketPath.StartsWith('/') || inbound.WebSocketPath.Any(char.IsControl)))
                problems.Add("proxy_server.config_invalid");
            else if (inbound.Protocol == ProxyServerProtocol.VlessReality && IPAddress.TryParse(inbound.HandshakeHost, out var address) && !PublicAddress(address))
                problems.Add("proxy_server.handshake_address_forbidden");
        }
        return new(problems.Count == 0, problems.Distinct().ToArray());
    }

    public static bool Host(string? value) => value is { Length: > 0 and <= 253 }
        && !value.Any(char.IsWhiteSpace) && value.IndexOfAny(['/', '\\', '@', '?', '#', '%']) < 0
        && Uri.CheckHostName(value) != UriHostNameType.Unknown
        && value is not ("0.0.0.0" or "::") && !value.Equals("localhost", StringComparison.OrdinalIgnoreCase);

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

    public static string Render(ProxyServerMaterial material, bool checkCertificateDates = true)
    {
        if (!Validate(material.Configuration).Valid || material.Credentials.Count > 1024
            || material.Credentials.Select(c => c.Id).Distinct().Count() != material.Credentials.Count
            || material.Credentials.Any(c => c.Id == Guid.Empty || c.Value.Length is < 32 or > 128
                || !material.Configuration.Inbounds.Any(i => i.Id == c.InboundId))
            || material.Keys.GroupBy(k => k.InboundId).Any(g => g.Count() != 1)) throw new InvalidDataException("proxy_server.config_invalid");
        var inbounds = new List<object>();
        foreach (var inbound in material.Configuration.Inbounds.Where(i => i.Enabled))
        {
            var users = material.Credentials.Where(c => c.InboundId == inbound.Id).ToArray();
            var vless = inbound.Protocol is ProxyServerProtocol.VlessReality or ProxyServerProtocol.VlessTls or ProxyServerProtocol.VlessWebSocket;
            if (vless && users.Any(c => !Guid.TryParseExact(c.Value, "D", out _))) throw new InvalidDataException("proxy_server.credential_invalid");
            var tls = new Dictionary<string, object> { ["enabled"] = true, ["server_name"] = inbound.ServerName };
            if (inbound.Protocol == ProxyServerProtocol.VlessReality)
            {
                var key = material.Keys.SingleOrDefault(k => k.InboundId == inbound.Id) ?? throw new InvalidDataException("proxy_server.keys_missing");
                if (!Key(key.PrivateKey) || !Key(key.PublicKey) || key.ShortId.Length != 16 || !key.ShortId.All(Uri.IsHexDigit)) throw new InvalidDataException("proxy_server.keys_invalid");
                tls["reality"] = new { enabled = true, handshake = new { server = inbound.HandshakeHost, server_port = inbound.HandshakePort }, private_key = key.PrivateKey, short_id = new[] { key.ShortId } };
            }
            else
            {
                var certificate = material.Certificates?.SingleOrDefault(c => c.InboundId == inbound.Id) ?? throw new InvalidDataException("proxy_server.certificate_invalid");
                using var parsed = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(certificate.CertificatePem, certificate.PrivateKeyPem);
                if (checkCertificateDates && (parsed.NotAfter.ToUniversalTime() <= DateTime.UtcNow || parsed.NotBefore.ToUniversalTime() > DateTime.UtcNow)
                    || !parsed.MatchesHostname(inbound.ServerName)) throw new InvalidDataException("proxy_server.certificate_invalid");
                tls["certificate"] = new[] { certificate.CertificatePem };
                tls["key"] = new[] { certificate.PrivateKeyPem };
            }
            var item = new Dictionary<string, object>
            {
                ["type"] = vless ? "vless" : inbound.Protocol == ProxyServerProtocol.Trojan ? "trojan" : "hysteria2",
                ["tag"] = inbound.Id.ToString("N"), ["listen"] = inbound.ListenAddress, ["listen_port"] = inbound.ListenPort,
                ["users"] = users.Select(c => vless ? (object)new { name = c.Id.ToString("N"), uuid = c.Value,
                    flow = inbound.Protocol == ProxyServerProtocol.VlessReality ? "xtls-rprx-vision" : "" } : new { name = c.Id.ToString("N"), password = c.Value }).ToArray(),
                ["tls"] = tls
            };
            if (inbound.Protocol == ProxyServerProtocol.VlessWebSocket) item["transport"] = new { type = "ws", path = inbound.WebSocketPath };
            inbounds.Add(item);
        }
        return JsonSerializer.Serialize(new
        {
            log = new { level = "error", timestamp = true },
            dns = new { servers = new[] { new { type = "local", tag = "local" } } },
            inbounds,
            outbounds = new[] { new { type = "direct", tag = "direct" } },
            route = new { default_domain_resolver = "local", rules = new object[] {
                new { action = "resolve" },
                new { ip_is_private = true, action = "reject" },
                new { ip_cidr = new[] { "0.0.0.0/8", "100.64.0.0/10", "169.254.0.0/16", "224.0.0.0/4", "::/128", "ff00::/8" }, action = "reject" }
            }, final = "direct" }
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    public static bool Key(string value) => value.Length == 43 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
