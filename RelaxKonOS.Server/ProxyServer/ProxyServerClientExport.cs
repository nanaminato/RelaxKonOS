using System.Globalization;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.ProxyServer;

namespace RelaxKonOS.Server.ProxyServer;

/// <summary>
/// Builds client connection material from the applied configuration only. Every format contains the
/// complete structure a client needs; a bare outbound or proxy fragment is never offered.
/// </summary>
public static class ProxyServerClientExport
{
    private sealed record Node(ProxyServerInbound Inbound, string Tag);

    public static IReadOnlyList<ProxyServerExportFormatOption> Formats(ProxyServerMaterial material, ProxyServerSecret credential)
    {
        var nodes = Nodes(material, credential);
        var multiple = nodes.Count > 1;
        return ProxyServerExportFormats.All.Select(format => format switch
        {
            ProxyServerExportFormats.Uri => new ProxyServerExportFormatOption(format, !multiple,
                multiple ? "proxy_server.export_multiple_inbounds" : null),
            _ => new ProxyServerExportFormatOption(format, nodes.Count > 0,
                nodes.Count == 0 ? "proxy_server.export_unsupported" : null)
        }).ToArray();
    }

    public static ProxyServerExport Create(ProxyServerMaterial material, ProxyServerSecret credential, string name, string format)
    {
        if (!ProxyServerExportFormats.IsKnown(format)) throw new ProxyServerException("proxy_server.export_unsupported");
        var nodes = Nodes(material, credential);
        if (nodes.Count == 0) throw new ProxyServerException("proxy_server.export_unsupported");
        var partial = credential.InboundIds.Any(id => material.Configuration.Inbounds.All(i => i.Id != id || !i.Enabled));
        var warning = partial ? "proxy_server.export_partial" : null;
        return format switch
        {
            ProxyServerExportFormats.Uri => new(format, string.Join('\n', nodes.Select(node => Uri(material, node, credential, name))), warning),
            ProxyServerExportFormats.SingBox => new(format, SingBox(material, nodes, credential), warning),
            _ => new(format, Mihomo(material, nodes, credential), warning)
        };
    }

    private static List<Node> Nodes(ProxyServerMaterial material, ProxyServerSecret credential)
    {
        var used = new HashSet<string>(["proxy", "direct", "PROXY", "mixed-in"], StringComparer.OrdinalIgnoreCase);
        var nodes = new List<Node>();
        foreach (var inbound in material.Configuration.Inbounds.Where(i => i.Enabled && credential.InboundIds.Contains(i.Id)))
        {
            var tag = Tag(inbound.Name, used);
            nodes.Add(new Node(inbound, tag));
        }
        return nodes;
    }

    private static string Tag(string name, HashSet<string> used)
    {
        var baseTag = string.IsNullOrWhiteSpace(name) ? "node" : name.Trim();
        var tag = baseTag;
        for (var index = 2; !used.Add(tag); index++) tag = baseTag + " " + index.ToString(CultureInfo.InvariantCulture);
        return tag;
    }

    private static string Uri(ProxyServerMaterial material, Node node, ProxyServerSecret credential, string name)
    {
        var inbound = node.Inbound;
        var host = inbound.PublicHost.Contains(':') ? "[" + inbound.PublicHost + "]" : inbound.PublicHost;
        var key = inbound.Protocol == ProxyServerProtocol.VlessReality
            ? material.Keys.Single(k => k.InboundId == inbound.Id) : null;
        var options = new List<string>();
        if (ProxyServerPresets.IsVless(inbound.Protocol)) options.Add("encryption=none");
        switch (inbound.Protocol)
        {
            case ProxyServerProtocol.VlessReality:
                options.AddRange(["security=reality", "sni=" + Escape(inbound.ServerName), "fp=chrome",
                    "pbk=" + key!.PublicKey, "sid=" + key.ShortId, "flow=xtls-rprx-vision", "type=tcp"]);
                break;
            case ProxyServerProtocol.VlessTls:
                options.AddRange(["security=tls", "sni=" + Escape(inbound.ServerName), "fp=chrome", "type=tcp"]);
                break;
            case ProxyServerProtocol.VlessWebSocket:
                options.AddRange(["security=tls", "sni=" + Escape(inbound.ServerName), "fp=chrome", "type=ws",
                    "host=" + Escape(inbound.ServerName), "path=" + Escape(inbound.WebSocketPath)]);
                break;
            case ProxyServerProtocol.Trojan:
                options.AddRange(["security=tls", "sni=" + Escape(inbound.ServerName), "type=tcp"]);
                break;
            case ProxyServerProtocol.Hysteria2:
                options.AddRange(["sni=" + Escape(inbound.ServerName), "insecure=0"]);
                break;
        }
        var scheme = inbound.Protocol switch
        {
            ProxyServerProtocol.Trojan => "trojan",
            ProxyServerProtocol.Hysteria2 => "hysteria2",
            _ => "vless"
        };
        return $"{scheme}://{System.Uri.EscapeDataString(credential.Value)}@{host}:{inbound.PublicPort}?{string.Join('&', options)}#{Escape(name)}";
    }

    private static string Escape(string value) => System.Uri.EscapeDataString(value);

    private static string SingBox(ProxyServerMaterial material, IReadOnlyList<Node> nodes, ProxyServerSecret credential)
    {
        var outbounds = new List<object>();
        foreach (var node in nodes) outbounds.Add(Outbound(material, node, credential));
        outbounds.Add(new { type = "selector", tag = "proxy", outbounds = nodes.Select(n => n.Tag).ToArray(), @default = nodes[0].Tag });
        outbounds.Add(new { type = "direct", tag = "direct" });
        return JsonSerializer.Serialize(new
        {
            log = new { level = "info", timestamp = true },
            inbounds = new[] { new { type = "mixed", tag = "mixed-in", listen = "127.0.0.1", listen_port = 2080 } },
            outbounds,
            route = new { final = "proxy" }
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static Dictionary<string, object> Outbound(ProxyServerMaterial material, Node node, ProxyServerSecret credential)
    {
        var inbound = node.Inbound;
        var tls = new Dictionary<string, object> { ["enabled"] = true, ["server_name"] = inbound.ServerName };
        var outbound = new Dictionary<string, object>
        {
            ["type"] = inbound.Protocol == ProxyServerProtocol.Hysteria2 ? "hysteria2"
                : inbound.Protocol == ProxyServerProtocol.Trojan ? "trojan" : "vless",
            ["tag"] = node.Tag,
            ["server"] = inbound.PublicHost,
            ["server_port"] = inbound.PublicPort
        };
        if (ProxyServerPresets.IsVless(inbound.Protocol)) outbound["uuid"] = credential.Value;
        else outbound["password"] = credential.Value;
        switch (inbound.Protocol)
        {
            case ProxyServerProtocol.VlessReality:
                var key = material.Keys.Single(k => k.InboundId == inbound.Id);
                tls["utls"] = new { enabled = true, fingerprint = "chrome" };
                tls["reality"] = new { enabled = true, public_key = key.PublicKey, short_id = key.ShortId };
                outbound["flow"] = "xtls-rprx-vision";
                break;
            case ProxyServerProtocol.VlessWebSocket:
                outbound["transport"] = new { type = "ws", path = inbound.WebSocketPath, headers = new { Host = inbound.ServerName } };
                break;
        }
        outbound["tls"] = tls;
        return outbound;
    }

    private static string Mihomo(ProxyServerMaterial material, IReadOnlyList<Node> nodes, ProxyServerSecret credential)
    {
        var yaml = new StringBuilder();
        yaml.Append("mixed-port: 2080\nallow-lan: false\nmode: rule\nproxies:\n");
        foreach (var node in nodes) Proxy(material, node, credential, yaml);
        yaml.Append("proxy-groups:\n  - name: PROXY\n    type: select\n    proxies:\n");
        foreach (var node in nodes) yaml.Append("      - ").Append(Scalar(node.Tag)).Append('\n');
        yaml.Append("rules:\n  - MATCH,PROXY\n");
        return yaml.ToString();
    }

    /// <summary>
    /// Writes one proxy block. JSON string literals are valid YAML double-quoted scalars and escape
    /// control characters, so names and WebSocket paths cannot change the document structure.
    /// </summary>
    private static void Proxy(ProxyServerMaterial material, Node node, ProxyServerSecret credential, StringBuilder yaml)
    {
        var inbound = node.Inbound;
        var vless = ProxyServerPresets.IsVless(inbound.Protocol);
        yaml.Append("  - name: ").Append(Scalar(node.Tag)).Append('\n');
        yaml.Append("    type: ").Append(inbound.Protocol switch
        {
            ProxyServerProtocol.Hysteria2 => "hysteria2",
            ProxyServerProtocol.Trojan => "trojan",
            _ => "vless"
        }).Append('\n');
        yaml.Append("    server: ").Append(Scalar(inbound.PublicHost)).Append('\n');
        yaml.Append("    port: ").Append(inbound.PublicPort.ToString(CultureInfo.InvariantCulture)).Append('\n');
        yaml.Append("    udp: true\n");
        yaml.Append(vless ? "    uuid: " : "    password: ").Append(Scalar(credential.Value)).Append('\n');
        yaml.Append(inbound.Protocol == ProxyServerProtocol.Hysteria2 ? "    sni: " : "    servername: ")
            .Append(Scalar(inbound.ServerName)).Append('\n');
        if (inbound.Protocol != ProxyServerProtocol.Hysteria2) yaml.Append("    tls: true\n");
        switch (inbound.Protocol)
        {
            case ProxyServerProtocol.VlessReality:
                var key = material.Keys.Single(k => k.InboundId == inbound.Id);
                yaml.Append("    flow: xtls-rprx-vision\n");
                yaml.Append("    client-fingerprint: chrome\n");
                yaml.Append("    reality-opts:\n");
                yaml.Append("      public-key: ").Append(Scalar(key.PublicKey)).Append('\n');
                yaml.Append("      short-id: ").Append(Scalar(key.ShortId)).Append('\n');
                break;
            case ProxyServerProtocol.VlessWebSocket:
                yaml.Append("    network: ws\n");
                yaml.Append("    ws-opts:\n");
                yaml.Append("      path: ").Append(Scalar(inbound.WebSocketPath)).Append('\n');
                yaml.Append("      headers:\n");
                yaml.Append("        Host: ").Append(Scalar(inbound.ServerName)).Append('\n');
                break;
        }
    }

    private static string Scalar(string value) => JsonSerializer.Serialize(value);
}
