using System.Text.Json;
using RelaxKonOS.Protocol.ProxyServer;

namespace RelaxKonOS.Server.ProxyServer;

public static class ProxyServerClientExport
{
    public static ProxyServerExport Create(ProxyServerMaterial material, ProxyServerSecret credential, string name, string format)
    {
        var inbound = material.Configuration.Inbounds.Single(i => i.Id == credential.InboundId && i.Enabled);
        var key = material.Keys.SingleOrDefault(k => k.InboundId == inbound.Id);
        var vless = inbound.Protocol is ProxyServerProtocol.VlessReality or ProxyServerProtocol.VlessTls or ProxyServerProtocol.VlessWebSocket;
        var reality = inbound.Protocol == ProxyServerProtocol.VlessReality;
        var ws = inbound.Protocol == ProxyServerProtocol.VlessWebSocket;
        var type = vless ? "vless" : inbound.Protocol == ProxyServerProtocol.Trojan ? "trojan" : "hysteria2";
        var address = inbound.PublicHost.Contains(':') ? "[" + inbound.PublicHost + "]" : inbound.PublicHost;
        var options = new List<string> { "sni=" + Uri.EscapeDataString(inbound.ServerName) };
        if (vless) options.Add("encryption=none");
        options.Add("security=" + (reality ? "reality" : "tls"));
        if (reality) options.AddRange(["flow=xtls-rprx-vision", "fp=chrome", "pbk=" + key!.PublicKey, "sid=" + key.ShortId]);
        if (vless || type == "trojan") options.Add("type=" + (ws ? "ws" : "tcp"));
        if (ws) options.Add("path=" + Uri.EscapeDataString(inbound.WebSocketPath));
        if (format == "uri") return new(format, $"{type}://{Uri.EscapeDataString(credential.Value)}@{address}:{inbound.PublicPort}?{string.Join('&', options)}#{Uri.EscapeDataString(name)}");
        var tls = new Dictionary<string, object> { ["enabled"] = true, ["server_name"] = inbound.ServerName };
        if (reality)
        {
            tls["utls"] = new { enabled = true, fingerprint = "chrome" };
            tls["reality"] = new { enabled = true, public_key = key!.PublicKey, short_id = key.ShortId };
        }
        var outbound = new Dictionary<string, object> { ["type"] = type, ["tag"] = "proxy", ["server"] = inbound.PublicHost, ["server_port"] = inbound.PublicPort, ["tls"] = tls };
        outbound[vless ? "uuid" : "password"] = credential.Value;
        if (reality) outbound["flow"] = "xtls-rprx-vision";
        if (ws) outbound["transport"] = new { type = "ws", path = inbound.WebSocketPath };
        if (format == "sing-box") return new(format, JsonSerializer.Serialize(new {
            inbounds = new[] { new { type = "mixed", tag = "local", listen = "127.0.0.1", listen_port = 2080 } },
            outbounds = new[] { outbound }, route = new { final = "proxy" }
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (format != "mihomo") throw new ProxyServerException("proxy_server.export_unsupported");
        var proxy = new Dictionary<string, object> { ["name"] = "proxy", ["type"] = type, ["server"] = inbound.PublicHost, ["port"] = inbound.PublicPort, ["udp"] = true };
        proxy[vless ? "uuid" : "password"] = credential.Value;
        proxy[type == "hysteria2" ? "sni" : "servername"] = inbound.ServerName;
        if (type != "hysteria2") proxy["tls"] = true;
        if (reality)
        {
            proxy["flow"] = "xtls-rprx-vision"; proxy["client-fingerprint"] = "chrome";
            proxy["reality-opts"] = new Dictionary<string, object> { ["public-key"] = key!.PublicKey, ["short-id"] = key.ShortId };
        }
        if (ws) { proxy["network"] = "ws"; proxy["ws-opts"] = new Dictionary<string, object> { ["path"] = inbound.WebSocketPath }; }
        return new(format, new YamlDotNet.Serialization.SerializerBuilder().Build().Serialize(new Dictionary<string, object> {
            ["mixed-port"] = 2080, ["allow-lan"] = false, ["proxies"] = new[] { proxy },
            ["proxy-groups"] = new[] { new Dictionary<string, object> { ["name"] = "PROXY", ["type"] = "select", ["proxies"] = new[] { "proxy" } } },
            ["rules"] = new[] { "MATCH,PROXY" }
        }));
    }
}
