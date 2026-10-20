using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.PrivilegedHelper;

internal static class MihomoConfigurationPolicyChecks
{
    internal static int Run()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        void Reject(string yaml, string code = "runtime.native_configuration_invalid")
        {
            try { MihomoYaml.Parse(yaml); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == code) { count++; return; }
            throw new Exception("Expected YAML refusal: " + code);
        }
        const string secret = "controlled-configuration-secret";
        string Managed(string profile) => MihomoManagedConfiguration.WithRuntimeSettings(
            MihomoManagedConfiguration.WithServerControllerSettings(
                MihomoManagedConfiguration.WithServerGeoDataSettings(profile), new(), secret),
            new ProxySettingsDto(false, false, false, false, false, "info", 17890));
        void PolicyReject(string profile, string code)
        {
            try { MihomoConfigurationPolicy.Describe(Managed(profile)); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == code) { count++; return; }
            throw new Exception("Expected configuration policy refusal: " + code);
        }
        var empty = Managed("");
        Check(MihomoConfigurationPolicy.Describe(empty).Material.ControllerSecret == secret, "Empty bootstrap becomes one valid managed mapping");
        Check(MihomoConfigurationPolicy.Describe(Managed("# no user settings yet\n")).Material.ControllerPort == 9090,
            "Comment-only bootstrap stays a valid empty configuration document");
        var inherited = Managed("defaults: &default { external-controller: '0.0.0.0:80', secret: untrusted }\n<<: *default\nrules: [MATCH,DIRECT]\n");
        var material = MihomoConfigurationPolicy.Describe(inherited).Material;
        Check(material.ControllerPort == 9090 && material.ControllerSecret == secret, "Controller ownership replaces merged and aliased keys structurally");
        var flow = Managed("{ 'external-controller': '0.0.0.0:80', 'secret': other, 'external-ui': '/etc', 'dns': { 'enable': true, nameserver: ['223.5.5.5'] } }");
        Check(MihomoConfigurationPolicy.Describe(flow).Material.ControllerSecret == secret
            && MihomoYaml.Get(MihomoYaml.Parse(flow), "external-ui") is null, "Flow/quoted alternate management fields cannot bypass Server ownership");
        var merged = MihomoYaml.Parse("first: &a {value: first}\nsecond: &b {value: second}\n<<: [*a, *b]\n");
        Check(MihomoYaml.Scalar(MihomoYaml.Get(merged, "value")) == "first", "First merge source has YAML merge precedence");
        Check(MihomoYaml.Scalar(MihomoYaml.Get(MihomoYaml.Parse("default: &d {value: old}\n<<: *d\nvalue: explicit\n"), "value")) == "explicit",
            "Explicit mapping keys override merged defaults");
        var literalMerge = MihomoYaml.Parse("'<<': {value: literal}\n");
        Check(MihomoYaml.Get(MihomoYaml.Parse(MihomoYaml.Render(literalMerge)), "value") is null,
            "A quoted merge-like key remains literal after normalization");
        var block = Managed("notes: |\n  secret: not-a-root-key\n  external-controller: 0.0.0.0:80\nrules: [MATCH,DIRECT]\n");
        Check(MihomoYaml.Scalar(MihomoYaml.Get(MihomoYaml.Parse(block), "notes"))!.Contains("not-a-root-key", StringComparison.Ordinal),
            "Block scalar contents are not mistaken for control keys");
        Reject("key: one\nkey: two\n");
        Reject("---\nkey: one\n---\nkey: two\n");
        Reject("root: &self {child: *self}\n");
        Reject("root: *missing\n");
        Reject("root: !custom value\n");
        Reject("root: !!binary ZGF0YQ==\n");
        Reject("[one,two]\n");
        Reject("root: " + new string('[', 65) + "value" + new string(']', 65));
        Reject("root: &data '" + new string('a', 200_000) + "'\nexpanded: [" + string.Join(',', Enumerable.Repeat("*data", 24)) + "]\n");
        var tun = MihomoManagedConfiguration.ReadTunActivation("tun: &tun {enable: true, route-exclude-address: ['192.168.1.0/24', '127.0.0.0/8']}\n");
        Check(tun.Enabled && tun.RouteExclusions.SequenceEqual(["192.168.1.0/24", "127.0.0.0/8"]), "Structured TUN parsing preserves inline safety exclusions");
        var actualTun = MihomoConfigurationPolicy.Describe(Managed("tun: {enable: true, stack: gvisor}\n")).Material;
        Check(actualTun.NetworkTransitionRequired && actualTun.RequiredBuildTags.Contains("with_gvisor"), "TUN requirements come from effective YAML rather than a caller flag");
        var noFlag = actualTun with { NetworkTransitionRequired = false };
        Check(MihomoConfigurationPolicy.Validate(noFlag).Material.NetworkTransitionRequired, "A false callback flag cannot hide actual network changes");
        try { MihomoConfigurationPolicy.Validate(material with { ControllerSecret = "another-controller-secret" }); throw new Exception("Forged controller accepted"); }
        catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime.authorization_changed") { count++; }
        try { MihomoConfigurationPolicy.Validate(material with { Listeners = [new("0.0.0.0", 17890)] }); throw new Exception("Forged listener accepted"); }
        catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime.authorization_changed") { count++; }
        PolicyReject("proxy-providers: {unsafe: {type: file, path: /etc/shadow}}\n", "runtime.profile_resource_path_forbidden");
        PolicyReject("proxy-providers: {unsafe: {type: file, path: proxy_providers/../../etc/shadow}}\n", "runtime.profile_resource_path_forbidden");
        PolicyReject("proxy-providers: {unsafe: {type: http, url: 'http://127.0.0.1/secret'}}\n", "runtime.profile_resource_preparation_required");
        PolicyReject("proxy-providers: {unsafe: {type: inline, payload: [{name: unsafe, type: ssh, private-key: /etc/shadow}]}}\n", "runtime.profile_resource_path_forbidden");
        PolicyReject("proxies: [{name: unsafe, type: trojan, certificate: /etc/shadow}]\n", "runtime.profile_resource_path_forbidden");
        Check(MihomoConfigurationPolicy.Describe(Managed("proxies: [{name: ws, type: vless, ws-opts: {path: /transport/ws}}]\n")).ResourcePaths.Count == 0,
            "WebSocket URL paths are not misclassified as filesystem references");
        Check(MihomoConfigurationPolicy.Describe(Managed("proxies: [{name: wg, type: wireguard, private-key: '/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA='}]\n")).ResourcePaths.Count == 0,
            "WireGuard inline keys are not misclassified as absolute filesystem paths");
        var resource = MihomoConfigurationPolicy.Describe(Managed("proxy-providers: {local: {type: file, path: ./proxy_providers/local.yaml}}\n"));
        Check(resource.ResourcePaths.SequenceEqual(["proxy_providers/local.yaml"]), "Provider references resolve only inside their protected resource category");
        var root = Path.Combine(Path.GetTempPath(), "mihomo-resource-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            try { MihomoConfigurationPolicy.VerifyResources(resource, root); throw new Exception("Missing resource accepted"); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime.profile_resource_preparation_required") { count++; }
            Directory.CreateDirectory(Path.Combine(root, "proxy_providers"));
            File.WriteAllText(Path.Combine(root, "proxy_providers", "local.yaml"), "proxies: []\n");
            MihomoConfigurationPolicy.VerifyResources(resource, root);
            Check(true, "Prepared regular provider resource is found in the protected data root");
            File.WriteAllText(Path.Combine(root, "proxy_providers", "local.yaml"), "proxies: [{name: nested, type: ssh, private-key: /etc/shadow}]\n");
            try { MihomoConfigurationPolicy.VerifyResources(resource, root); throw new Exception("Nested provider escaped credential scope"); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime.profile_resource_path_forbidden") { count++; }
            if (OperatingSystem.IsLinux())
            {
                var path = Path.Combine(root, "proxy_providers", "local.yaml");
                File.Delete(path);
                NativeRuntimeProcess.RunAsync("/usr/bin/mkfifo", [path], 1024).GetAwaiter().GetResult();
                try { MihomoConfigurationPolicy.VerifyResources(resource, root); throw new Exception("Special provider resource accepted"); }
                catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime.profile_resource_path_forbidden") { count++; }
                File.Delete(path);
            }
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)) throw new Exception("Resource cleanup escaped root");
            Directory.Delete(root, true);
        }
        return count;
    }
}
