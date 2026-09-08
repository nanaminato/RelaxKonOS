using System.Text;
using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.Server.Tunnels;

/// <summary>Small closed TOML writer. No caller can inject TOML keys, includes, CLI arguments, or environment templates.</summary>
internal static class FrpTomlGenerator
{
    public static string Generate(TunnelServerProfileDto profile, IEnumerable<TunnelDefinitionDto> definitions, string? token)
    {
        var text = new StringBuilder();
        Line(text, "serverAddr", profile.Host); text.Append("serverPort = ").Append(profile.Port).AppendLine();
        if (profile.AuthKind == TunnelAuthKind.Token)
        {
            if (string.IsNullOrEmpty(token)) throw new TunnelValidationException("tunnel.token_required");
            text.AppendLine("[auth]"); Line(text, "method", "token"); Line(text, "token", token);
        }
        if (profile.TlsMode != TunnelTlsMode.Default)
        {
            text.AppendLine("[transport.tls]"); text.Append("enable = ").Append(profile.TlsMode == TunnelTlsMode.Force ? "true" : "false").AppendLine();
        }
        foreach (var tunnel in definitions.Where(x => x.Enabled).OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            text.AppendLine(); text.AppendLine("[[proxies]]"); Line(text, "name", tunnel.Name); Line(text, "type", tunnel.Protocol.ToString().ToLowerInvariant());
            Line(text, "localIP", tunnel.LocalHost); text.Append("localPort = ").Append(tunnel.LocalPort).AppendLine();
            if (tunnel.RemotePort is { } remotePort) text.Append("remotePort = ").Append(remotePort).AppendLine();
            if (tunnel.Domain is { } domain) Line(text, "customDomains", domain, array: true);
            if (tunnel.Encryption || tunnel.Compression)
            {
                text.AppendLine("[proxies.transport]");
                if (tunnel.Encryption) text.AppendLine("useEncryption = true");
                if (tunnel.Compression) text.AppendLine("useCompression = true");
            }
        }
        return text.ToString();
    }

    private static void Line(StringBuilder builder, string key, string value, bool array = false)
    {
        builder.Append(key).Append(" = ");
        if (array) builder.Append('[');
        builder.Append('"').Append(Escape(value)).Append('"');
        if (array) builder.Append(']');
        builder.AppendLine();
    }
    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
