using System.Text.Json;
using RelaxKonOS.ManagedRuntimes.Catalog;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.Components;

internal static class ComponentCatalogChecks
{
    public static async Task RunAsync()
    {
        await ComponentReleaseProviderChecks.RunAsync();
        var catalog = BundledComponentCatalog.Current;
        Check(catalog.Components.Count == 10, "all existing components ship as manifests");
        var nginx = catalog.Components.Single(component => component.Id == "nginx");
        var mihomo = catalog.Components.Single(component => component.Id == "mihomo");
        Check(CompatibilityResolver.Resolve(nginx, "linux", "x64", target => target.Installer.Type == "apt").Status == ComponentCompatibility.Supported, "Linux nginx selects APT");
        Check(CompatibilityResolver.Resolve(nginx, "windows", "arm64", target => target.Installer.Type == "archive").Target?.Installer.Type == "archive", "Windows nginx selects archive");
        Check(CompatibilityResolver.Resolve(mihomo, "linux", "arm64", _ => true).Target is
            { OfficialDownloads: false, Installer: { Type: "archive", Available: true } }, "Linux Mihomo keeps signed archive plans with official direct install closed");
        Check(CompatibilityResolver.Resolve(mihomo, "windows", "arm64", _ => true).Target?.OfficialDownloads == true,
            "Windows Mihomo retains official archive downloads");
        Check(CompatibilityResolver.Resolve(nginx, "linux", "x86", _ => true).Status == ComponentCompatibility.UnsupportedPlatform, "architecture mismatch refuses");
        Check(CompatibilityResolver.Resolve(nginx, "macos", "x64", _ => true).Status == ComponentCompatibility.UnsupportedPlatform, "OS mismatch refuses");
        Check(CompatibilityResolver.Resolve(nginx, "linux", null, _ => true).Status == ComponentCompatibility.Unknown, "missing host facts remain unknown");
        Check(CompatibilityResolver.Resolve(nginx, "linux", "x64", _ => false).Status == ComponentCompatibility.ProviderUnavailable, "missing provider refuses");
        Check(CompatibilityResolver.Resolve(nginx, "linux", "x64", _ => true, false).Status == ComponentCompatibility.PolicyBlocked, "policy refuses independently");

        var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".tmp", "component-catalog-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(root, "manifests"));
        try
        {
            var source = new LocalCatalogSource(root, "test");
            var indexPath = Path.Combine(root, "catalog.json");
            var manifestPath = Path.Combine(root, "manifests", "nginx.json");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "ComponentCatalog", "manifests", "nginx.json"), manifestPath);
            const string index = """{"schemaVersion":1,"components":[{"id":"nginx","manifest":"./manifests/nginx.json"}],"workloads":[]}""";
            File.WriteAllText(indexPath, index);
            Check(source.Load().Components.Single().Id == "nginx", "local relative manifest loads without network");
            Check(source.ResolveReference("../catalog.json", "manifests/nginx.json") == indexPath, "manifest-relative parent inside root is safe");
            foreach (var reference in new[] { "../outside.json", "/etc/passwd", "C:/Windows/file", "//host/share", "https://example.com/a", "manifests\\nginx.json", "manifests/%2e%2e/a", "manifests/nginx.json:stream", "./manifests/nginx.json?x" })
                InvalidReference(() => source.ResolveReference(reference));
            foreach (var invalid in new[] {
                index.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
                index.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
                index.Replace("\"workloads\":[]", "\"workloads\":[],\"command\":\"sh\""),
                index.Replace("\"id\":\"nginx\"", "\"id\":\"other\""),
                index.Replace("./manifests/nginx.json", "./missing.json") })
            {
                File.WriteAllText(indexPath, invalid);
                Refuses(() => source.Load(), "invalid index refuses");
            }
            File.WriteAllText(indexPath, index);
            var manifest = File.ReadAllText(manifestPath);
            foreach (var invalid in new[] {
                manifest.Replace("\"type\": \"apt\"", "\"type\": \"shell\""),
                manifest.Replace("\"type\": \"apt\"", "\"type\": \"apt\", \"command\": \"curl | sh\""),
                manifest.Replace("\"architectures\": [", "\"powershell\": \"whoami\", \"architectures\": ["),
                manifest.Replace("\"id\": \"nginx\"", "\"id\": null"),
                manifest.Replace("\"os\": \"linux\"", "\"os\": \"linux\", \"os\": \"linux\""),
                new string(' ', 256 * 1024 + 1) })
            {
                File.WriteAllText(manifestPath, invalid);
                Refuses(() => source.Load(), "invalid manifest refuses");
            }
            File.WriteAllText(manifestPath, manifest);
            var link = Path.Combine(root, "linked");
            try
            {
                Directory.CreateSymbolicLink(link, Path.Combine(root, "manifests"));
                InvalidReference(() => source.ResolveReference("linked/nginx.json"));
                Directory.Delete(link);
            }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
            { Console.WriteLine("SKIP directory symlink creation: Windows host lacks symlink privilege."); }
        }
        finally
        {
            var parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".tmp")) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe cleanup.");
            Directory.Delete(root, true);
        }

        var request = new ComponentPlanRequest("nginx", "host", RuntimeChange.Install, 17,
            ComponentSourceMode.SystemPackage, "source", 31, 42, "release", "1.2.3", true, "frozen-key");
        var domain = new Probe();
        var registry = ComponentInstallerProviderRegistry.Host;
        var selected = registry.Resolve(domain, request, catalog, "linux", "x64");
        Check(selected.Provider?.Type == "apt", "mechanism registry selects existing package adapter");
        await selected.Provider!.PlanAsync(domain, request, default);
        Check(ReferenceEquals(domain.Request, request), "provider preserves source, version, revision and idempotency request");
        var frozen = new ComponentExecuteRequest("nginx", request, null);
        await selected.Provider.ExecuteAsync(domain, frozen, default);
        Check(ReferenceEquals(domain.Frozen, frozen), "execution forwards the frozen domain expectation without resolving latest");
        Check(registry.Find("shell") is null && registry.Find("roapp") is null, "Server cannot execute scripts or desktop packages");
        Check(registry.Resolve(domain, request, catalog, "linux", "x86").Provider is null, "dispatch blocks wrong architecture");
        Check(new ComponentInstallerProviderRegistry([]).Resolve(domain, request, catalog, "linux", "x64").Provider is null,
            "a declaration cannot create a missing provider");
        Check(registry.Resolve(domain, request, new([], []), "linux", "x64").Provider is null, "install needs catalog entry");
        Check(registry.Resolve(domain, request with { Change = RuntimeChange.Uninstall }, new([], []), "linux", "x64").Provider is not null,
            "uninstall dispatch survives catalog removal; domain still enforces ownership");
        Console.WriteLine("Component catalog and provider checks passed.");
    }

    private static void Refuses(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException) { return; }
        throw new Exception("Expected refusal: " + message);
    }
    private static void InvalidReference(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Unsafe reference must fail policy validation before filesystem lookup.");
    }
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); }

    private sealed class Probe : IComponentLifecycleAdapter
    {
        public string ComponentId => "nginx";
        public IReadOnlyList<ComponentModeSupport> Modes => [new(ComponentSourceMode.SystemPackage, "linux", true, null)];
        public ComponentPlanRequest? Request;
        public ComponentExecuteRequest? Frozen;
        public Task<ComponentStateView> StateAsync(CancellationToken ct) => Task.FromResult(new ComponentStateView(ComponentId, null, Modes, []));
        public Task<ComponentPlanResult> PlanAsync(ComponentPlanRequest request, CancellationToken ct)
        { Request = request; return Task.FromResult(new ComponentPlanResult(request, null, null, ["domain.refused"], [])); }
        public Task<ComponentExecutionResult> ExecuteAsync(ComponentExecuteRequest frozen, CancellationToken ct)
        { Frozen = frozen; return Task.FromResult(new ComponentExecutionResult(ComponentId, ComponentExecutionOutcome.Refused, ["domain.refused"], [], null, null)); }
    }
}
