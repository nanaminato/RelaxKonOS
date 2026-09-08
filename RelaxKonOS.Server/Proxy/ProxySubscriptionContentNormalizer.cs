using System.Text;
using System.Text.Json;

namespace RelaxKonOS.Server.Proxy;

/// <summary>
/// Normalizes a remote subscription into the YAML profile format consumed by Mihomo.
/// Native YAML stays untouched; universal Base64 subscriptions are decoded and their
/// supported URI entries are rendered as a minimal <c>proxies</c> configuration.
/// </summary>
internal static class ProxySubscriptionContentNormalizer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Normalize(string content)
    {
        if (LooksLikeYaml(content)) return content;

        var candidate = TryDecodeBase64Text(content) ?? content;
        if (LooksLikeYaml(candidate)) return candidate;
        return TryConvertLinks(candidate, out var yaml) ? yaml : content;
    }

    private static bool TryConvertLinks(string content, out string yaml)
    {
        var proxies = new StringBuilder();
        var count = 0;
        foreach (var line in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var link = line.Trim().TrimStart('\uFEFF');
            if (link.Length == 0 || link.StartsWith('#')) continue;
            if (!TryConvertLink(link, out var proxy)) continue;
            proxies.Append(proxy);
            count++;
        }

        yaml = count == 0 ? string.Empty : "proxies:\n" + proxies;
        return count > 0;
    }

    private static bool TryConvertLink(string link, out string yaml)
    {
        yaml = string.Empty;
        var separator = link.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0) return false;
        var scheme = link[..separator].ToLowerInvariant();
        return scheme switch
        {
            "vmess" => TryConvertVmess(link, out yaml),
            "vless" => TryConvertUri(link, "vless", out yaml),
            "trojan" => TryConvertUri(link, "trojan", out yaml),
            "ss" => TryConvertShadowsocks(link, out yaml),
            "hysteria2" or "hy2" => TryConvertUri(link, "hysteria2", out yaml),
            "tuic" => TryConvertUri(link, "tuic", out yaml),
            _ => false,
        };
    }

    private static bool TryConvertVmess(string link, out string yaml)
    {
        yaml = string.Empty;
        var payload = link["vmess://".Length..];
        var hash = payload.IndexOf('#');
        if (hash >= 0) payload = payload[..hash];
        var decoded = TryDecodeBase64Text(Uri.UnescapeDataString(payload));
        if (decoded is null) return false;

        try
        {
            using var json = JsonDocument.Parse(decoded);
            var root = json.RootElement;
            var server = GetJsonString(root, "add");
            var uuid = GetJsonString(root, "id");
            if (!TryPort(GetJsonString(root, "port"), out var port) || string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(uuid)) return false;

            var builder = StartProxy(GetJsonString(root, "ps") ?? server, "vmess", server, port);
            AddString(builder, "uuid", uuid);
            AddNumber(builder, "alterId", GetJsonString(root, "aid"), "0");
            AddString(builder, "cipher", GetJsonString(root, "scy") ?? "auto");
            AppendTransport(builder, GetJsonString(root, "net"), GetJsonString(root, "path"), GetJsonString(root, "host"));
            AppendTls(builder, GetJsonString(root, "tls"), GetJsonString(root, "sni"), GetJsonString(root, "fp"), null, null, false);
            yaml = builder.ToString();
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryConvertUri(string link, string type, out string yaml)
    {
        yaml = string.Empty;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || !TryPort(uri.Port, out var port) || string.IsNullOrWhiteSpace(uri.Host)) return false;
        var query = ParseQuery(uri.Query);
        var name = DecodeFragment(uri.Fragment) ?? uri.Host;
        var userInfo = Uri.UnescapeDataString(uri.UserInfo);
        if (string.IsNullOrWhiteSpace(userInfo)) return false;

        var builder = StartProxy(name, type, uri.Host, port);
        switch (type)
        {
            case "vless":
                AddString(builder, "uuid", userInfo);
                AppendTransport(builder, Value(query, "type") ?? "tcp", Value(query, "path"), Value(query, "host"));
                AppendTls(builder, Value(query, "security"), Value(query, "sni"), Value(query, "fp"), Value(query, "pbk"), Value(query, "sid"), IsTrue(Value(query, "allowInsecure")));
                AddString(builder, "flow", Value(query, "flow"));
                break;
            case "trojan":
                AddString(builder, "password", userInfo);
                AppendTransport(builder, Value(query, "type") ?? "tcp", Value(query, "path"), Value(query, "host"));
                AppendTls(builder, "tls", Value(query, "sni"), Value(query, "fp"), null, null, IsTrue(Value(query, "allowInsecure")));
                break;
            case "hysteria2":
                AddString(builder, "password", userInfo);
                AddString(builder, "sni", Value(query, "sni"));
                AddBoolean(builder, "skip-cert-verify", IsTrue(Value(query, "insecure")) || IsTrue(Value(query, "allowInsecure")));
                AddString(builder, "obfs", Value(query, "obfs"));
                AddString(builder, "obfs-password", Value(query, "obfs-password"));
                break;
            case "tuic":
                var credentials = userInfo.Split(':', 2);
                if (credentials.Length != 2) return false;
                AddString(builder, "uuid", credentials[0]);
                AddString(builder, "password", credentials[1]);
                AddString(builder, "sni", Value(query, "sni"));
                AddString(builder, "congestion-controller", Value(query, "congestion_control"));
                AddBoolean(builder, "skip-cert-verify", IsTrue(Value(query, "allowInsecure")) || IsTrue(Value(query, "insecure")));
                break;
        }

        yaml = builder.ToString();
        return true;
    }

    private static bool TryConvertShadowsocks(string link, out string yaml)
    {
        yaml = string.Empty;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || !TryPort(uri.Port, out var port) || string.IsNullOrWhiteSpace(uri.Host)) return false;
        var credentials = Uri.UnescapeDataString(uri.UserInfo);
        credentials = TryDecodeBase64Text(credentials) ?? credentials;
        var separator = credentials.IndexOf(':');
        if (separator <= 0 || separator == credentials.Length - 1) return false;

        var builder = StartProxy(DecodeFragment(uri.Fragment) ?? uri.Host, "ss", uri.Host, port);
        AddString(builder, "cipher", credentials[..separator]);
        AddString(builder, "password", credentials[(separator + 1)..]);
        yaml = builder.ToString();
        return true;
    }

    private static StringBuilder StartProxy(string name, string type, string server, int port)
    {
        var builder = new StringBuilder();
        builder.Append("  - name: ").Append(YamlString(name)).Append('\n');
        builder.Append("    type: ").Append(type).Append('\n');
        builder.Append("    server: ").Append(YamlString(server)).Append('\n');
        builder.Append("    port: ").Append(port).Append('\n');
        return builder;
    }

    private static void AppendTransport(StringBuilder builder, string? network, string? path, string? host)
    {
        if (string.IsNullOrWhiteSpace(network) || network.Equals("tcp", StringComparison.OrdinalIgnoreCase)) return;
        AddString(builder, "network", network);
        if (network.Equals("ws", StringComparison.OrdinalIgnoreCase))
        {
            builder.Append("    ws-opts:\n");
            AddNestedString(builder, "path", path ?? "/");
            if (!string.IsNullOrWhiteSpace(host))
            {
                builder.Append("      headers:\n");
                builder.Append("        Host: ").Append(YamlString(host)).Append('\n');
            }
        }
        else if (network.Equals("grpc", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(path))
        {
            builder.Append("    grpc-opts:\n");
            AddNestedString(builder, "grpc-service-name", path.TrimStart('/'));
        }
    }

    private static void AppendTls(StringBuilder builder, string? security, string? sni, string? fingerprint, string? publicKey, string? shortId, bool skipCertificateVerification)
    {
        var isReality = string.Equals(security, "reality", StringComparison.OrdinalIgnoreCase);
        var isTls = isReality || string.Equals(security, "tls", StringComparison.OrdinalIgnoreCase);
        if (!isTls) return;
        AddBoolean(builder, "tls", true);
        AddString(builder, "servername", sni);
        AddString(builder, "client-fingerprint", fingerprint);
        if (skipCertificateVerification) AddBoolean(builder, "skip-cert-verify", true);
        if (isReality)
        {
            builder.Append("    reality-opts:\n");
            AddNestedString(builder, "public-key", publicKey);
            AddNestedString(builder, "short-id", shortId);
        }
    }

    private static void AddString(StringBuilder builder, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) builder.Append("    ").Append(key).Append(": ").Append(YamlString(value)).Append('\n');
    }

    private static void AddNestedString(StringBuilder builder, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) builder.Append("      ").Append(key).Append(": ").Append(YamlString(value)).Append('\n');
    }

    private static void AddNumber(StringBuilder builder, string key, string? value, string fallback)
    {
        builder.Append("    ").Append(key).Append(": ")
            .Append(int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number >= 0 ? number : fallback)
            .Append('\n');
    }

    private static void AddBoolean(StringBuilder builder, string key, bool value)
    {
        if (value) builder.Append("    ").Append(key).Append(": true\n");
    }

    private static string? TryDecodeBase64Text(string value)
    {
        var compact = new string(value.Where(character => !char.IsWhiteSpace(character)).ToArray())
            .Replace('-', '+').Replace('_', '/');
        if (compact.Length == 0) return null;
        var remainder = compact.Length % 4;
        if (remainder == 1) return null;
        if (remainder != 0) compact = compact.PadRight(compact.Length + 4 - remainder, '=');
        try { return StrictUtf8.GetString(Convert.FromBase64String(compact)); }
        catch (FormatException) { return null; }
        catch (DecoderFallbackException) { return null; }
    }

    private static bool LooksLikeYaml(string content)
    {
        foreach (var line in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Take(64))
        {
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line.StartsWith('#')) continue;
            var separator = line.IndexOf(':');
            if (separator < 0) continue;
            var key = line[..separator];
            if (key is "proxies" or "proxy-providers" or "proxy-groups" or "mixed-port" or "port" or "mode" or "rules" or "dns") return true;
        }
        return false;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = item.IndexOf('=');
            var key = separator < 0 ? item : item[..separator];
            var value = separator < 0 ? string.Empty : item[(separator + 1)..];
            values[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
        }
        return values;
    }

    private static string? Value(IReadOnlyDictionary<string, string> values, string key) => values.GetValueOrDefault(key);
    private static string? DecodeFragment(string fragment) => string.IsNullOrWhiteSpace(fragment) ? null : Uri.UnescapeDataString(fragment.TrimStart('#'));
    private static bool IsTrue(string? value) => value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
    private static string? GetJsonString(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.ToString() : null;
    private static bool TryPort(string? value, out int port) => int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port) && TryPort(port, out port);
    private static bool TryPort(int value, out int port) { port = value; return value is >= 1 and <= 65535; }
    private static string YamlString(string value) => '"' + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + '"';
}
