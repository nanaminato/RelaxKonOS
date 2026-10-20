using System.Globalization;
using System.Net;
using System.Text;
using System.Runtime.InteropServices;
using RelaxKonOS.Protocol.ManagedRuntimes;
using YamlDotNet.RepresentationModel;

namespace RelaxKonOS.ManagedRuntimes;

public sealed record MihomoConfigurationDescription(MihomoPublishedRuntime Material, IReadOnlyList<string> ResourcePaths,
    IReadOnlyList<string> ProxyProviderPaths);

/// <summary>Re-derives the fixed controller, listeners, network needs and local resource scope from effective YAML.</summary>
public static class MihomoConfigurationPolicy
{
    private static readonly string[] Forbidden = ["external-controller-tls", "external-controller-unix", "external-controller-pipe",
        "external-ui", "external-ui-url", "external-ui-name", "external-ui-download-url", "geox-url"];

    public static MihomoConfigurationDescription Validate(MihomoPublishedRuntime published)
    {
        MihomoRuntimeMaterial.Validate(published);
        var description = Describe(published.Yaml);
        var actual = description.Material;
        if (actual.ControllerPort != published.ControllerPort || actual.ControllerSecret != published.ControllerSecret
            || actual.Listeners.Count != published.Listeners.Count || actual.Listeners.Any(listener => !published.Listeners.Contains(listener))
            || actual.RequiredBuildTags.Any(tag => !published.RequiredBuildTags.Contains(tag, StringComparer.Ordinal)))
            throw new RuntimePolicyException("runtime.authorization_changed");
        return description;
    }

    public static MihomoConfigurationDescription Describe(string yaml)
    {
        var root = MihomoYaml.Parse(yaml);
        foreach (var key in Forbidden) if (MihomoYaml.Get(root, key) is not null) Fail("configuration_unsupported");
        if (Flag(root, "geo-auto-update") || Flag(root, "geodata-mode")) Fail("profile_resource_preparation_required");
        var controller = Text(root, "external-controller");
        if (controller is null) Fail("configuration_unsupported");
        if (!IPEndPoint.TryParse(controller, out var endpoint) || !endpoint.Address.Equals(IPAddress.Loopback)
            || controller != "127.0.0.1:" + endpoint.Port.ToString(CultureInfo.InvariantCulture) || endpoint.Port == 0)
            Fail("configuration_unsupported");
        var secret = Text(root, "secret") ?? "";
        var bind = Text(root, "bind-address") ?? (Flag(root, "allow-lan") ? "*" : "127.0.0.1");
        if (bind != "*" && (!IPAddress.TryParse(bind, out var address) || bind != address.ToString())) Fail("configuration_unsupported");
        var listeners = new List<MihomoRuntimeListener>();
        foreach (var key in new[] { "mixed-port", "port", "socks-port", "redir-port", "tproxy-port" })
        {
            var port = Port(root, key);
            if (port > 0) listeners.Add(new(bind, port));
            if (key == "tproxy-port" && port > 0) listeners.Add(new(bind, port, Udp: true));
        }
        if (MihomoYaml.Get(root, "listeners") is { } extra && extra is not YamlSequenceNode { Children.Count: 0 })
            Fail("configuration_unsupported"); // Custom inbound protocols need their own listener/health adapter.
        if (MihomoYaml.Get(root, "dns") is { } dnsNode)
        {
            if (dnsNode is not YamlMappingNode dns) { Fail("native_configuration_invalid"); return null!; }
            if (Flag(dns, "enable") && Text(dns, "listen") is { Length: > 0 } listen)
            {
                if (!IPEndPoint.TryParse(listen, out var dnsEndpoint) || dnsEndpoint.Port == 0) Fail("configuration_unsupported");
                listeners.Add(new(dnsEndpoint.Address.ToString(), dnsEndpoint.Port));
                listeners.Add(new(dnsEndpoint.Address.ToString(), dnsEndpoint.Port, Udp: true));
            }
        }
        var network = Port(root, "redir-port") > 0 || Port(root, "tproxy-port") > 0;
        var tags = new List<string>();
        if (MihomoYaml.Get(root, "tun") is { } tunNode)
        {
            if (tunNode is not YamlMappingNode tun) { Fail("native_configuration_invalid"); return null!; }
            if (Flag(tun, "enable"))
            {
                network = true;
                if (Text(tun, "stack") is null or "gvisor" or "mixed") tags.Add("with_gvisor");
            }
        }
        var resources = new HashSet<string>(StringComparer.Ordinal);
        var proxyProviders = new List<string>();
        Providers(root, "proxy-providers", "proxy_providers", resources, proxyProviders);
        Providers(root, "rule-providers", "rule_providers", resources, proxyProviders);
        if (MihomoYaml.Get(root, "proxies") is { } proxiesNode)
        {
            if (proxiesNode is not YamlSequenceNode proxies) { Fail("native_configuration_invalid"); return null!; }
            foreach (var proxyNode in proxies.Children)
            {
                if (proxyNode is not YamlMappingNode proxy) { Fail("native_configuration_invalid"); continue; }
                CredentialFiles(proxy, resources, Text(proxy, "type") == "wireguard");
            }
        }
        if (MihomoYaml.Get(root, "tls") is { } tlsNode)
        {
            if (tlsNode is not YamlMappingNode tls) { Fail("native_configuration_invalid"); return null!; }
            CredentialFiles(tls, resources, wireguard: false);
        }
        var material = new MihomoPublishedRuntime(yaml, tags, endpoint.Port, secret, listeners.Distinct().ToArray(), network);
        MihomoRuntimeMaterial.Validate(material);
        if (resources.Count > 128) Fail("profile_resource_preparation_required");
        return new(material, resources.Order(StringComparer.Ordinal).ToArray(), proxyProviders);
    }

    public static void VerifyResources(MihomoConfigurationDescription description, string protectedData)
    {
        var resources = new HashSet<string>(description.ResourcePaths, StringComparer.Ordinal);
        foreach (var relative in description.ProxyProviderPaths)
        {
            var provider = ResourceFile(relative, protectedData);
            if (provider.Length > MihomoYaml.MaximumBytes) Fail("profile_resource_preparation_required");
            var root = MihomoYaml.Parse(ReadProvider(provider.FullName));
            if (MihomoYaml.Get(root, "proxies") is not YamlSequenceNode proxies) Fail("profile_resource_preparation_required");
            foreach (var node in ((YamlSequenceNode)MihomoYaml.Get(root, "proxies")!).Children)
            {
                if (node is not YamlMappingNode proxy) { Fail("native_configuration_invalid"); continue; }
                CredentialFiles(proxy, resources, Text(proxy, "type") == "wireguard");
            }
        }
        foreach (var relative in resources) ResourceFile(relative, protectedData);
    }

    private static FileInfo ResourceFile(string relative, string protectedData)
    {
        var path = Path.Combine(protectedData, relative.Replace('/', Path.DirectorySeparatorChar));
        RuntimeSourceStore.RequireNoLinks(path);
        if (!File.Exists(path)) Fail("profile_resource_preparation_required");
        if (OperatingSystem.IsLinux())
        {
            if (statx(-100, path, 0x100, 1, out var attributes) != 0 || (attributes.Mask & 1) == 0
                || (attributes.Mode & 0xf000) != 0x8000) Fail("profile_resource_path_forbidden");
        }
        else if ((File.GetAttributes(path) & FileAttributes.Device) != 0) Fail("profile_resource_path_forbidden");
        var file = new FileInfo(path);
        if (file.Length > MaximumResourceBytes) Fail("profile_resource_preparation_required");
        return file;
    }
    private const long MaximumResourceBytes = 16 * 1024 * 1024;

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct ResourceStat
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
    }
    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int directory, string path, int flags, uint mask, out ResourceStat state);

    private static string ReadProvider(string path)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MihomoYaml.MaximumBytes) Fail("profile_resource_preparation_required");
            var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            if (input.ReadByte() != -1) Fail("profile_resource_preparation_required");
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (Exception ex) when (ex is IOException or DecoderFallbackException)
        { throw new RuntimePolicyException("runtime.profile_resource_preparation_required"); }
    }

    private static void AddResource(HashSet<string> resources, string path)
    {
        if (resources.Add(path) && resources.Count > 128) Fail("profile_resource_preparation_required");
    }

    private static void Providers(YamlMappingNode root, string key, string prefix, HashSet<string> resources, List<string> proxyProviders)
    {
        if (MihomoYaml.Get(root, key) is not { } node) return;
        if (node is not YamlMappingNode providers) { Fail("native_configuration_invalid"); return; }
        foreach (var item in providers.Children.Values)
        {
            if (item is not YamlMappingNode provider) { Fail("native_configuration_invalid"); continue; }
            var type = Text(provider, "type");
            if (type == "inline")
            {
                if (key == "proxy-providers" && MihomoYaml.Get(provider, "payload") is YamlSequenceNode payload)
                    foreach (var proxy in payload.Children)
                        if (proxy is YamlMappingNode map) CredentialFiles(map, resources, Text(map, "type") == "wireguard");
                continue;
            }
            if (type != "file" || MihomoYaml.Get(provider, "url") is not null) Fail("profile_resource_preparation_required");
            var path = Resource(Text(provider, "path"), prefix);
            AddResource(resources, path);
            if (key == "proxy-providers") proxyProviders.Add(path);
        }
    }

    private static void CredentialFiles(YamlMappingNode map, HashSet<string> resources, bool wireguard)
    {
        foreach (var key in new[] { "ca", "certificate", "private-key" })
        {
            if (wireguard && key == "private-key") continue; // This protocol's key is inline base64, never a file.
            if (Text(map, key) is not { Length: > 0 } value || value.StartsWith("-----BEGIN ", StringComparison.Ordinal)) continue;
            AddResource(resources, Resource(value, key == "private-key" ? "keys" : "certificates"));
        }
        foreach (var key in new[] { "shadow-tls-opts", "restls-opts", "tls-opts", "ech-opts" })
            if (MihomoYaml.Get(map, key) is YamlMappingNode nested) CredentialFiles(nested, resources, wireguard: false);
        if (MihomoYaml.Get(map, "xhttp-opts") is YamlMappingNode transport
            && MihomoYaml.Get(transport, "download-settings") is YamlMappingNode download)
            CredentialFiles(download, resources, wireguard: false);
    }

    private static string Resource(string? path, string prefix)
    {
        var relative = path?.StartsWith("./", StringComparison.Ordinal) == true ? path[2..] : path;
        if (relative is null || !RuntimeArchiveRules.SafePath(relative) || !relative.StartsWith(prefix + "/", StringComparison.Ordinal))
            Fail("profile_resource_path_forbidden");
        return relative!;
    }
    private static string? Text(YamlMappingNode map, string key) => MihomoYaml.Scalar(MihomoYaml.Get(map, key));
    private static bool Flag(YamlMappingNode map, string key)
    {
        var text = Text(map, key);
        if (text is null) return false;
        if (!bool.TryParse(text, out var result)) Fail("native_configuration_invalid");
        return result;
    }
    private static int Port(YamlMappingNode map, string key)
    {
        var text = Text(map, key);
        if (text is null) return 0;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 0 or > 65535)
            Fail("native_configuration_invalid");
        return port;
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string code) => throw new RuntimePolicyException("runtime." + code);
}
