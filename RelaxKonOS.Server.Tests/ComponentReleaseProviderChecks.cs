using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.ManagedRuntimes.Catalog;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.Components;

internal static class ComponentReleaseProviderChecks
{
    public static async Task RunAsync()
    {
        var manifest = BundledComponentCatalog.Current.Components.Single(component => component.Id == "mihomo");
        var target = manifest.Targets.Single(value => value.Os == "linux");
        var host = new ComponentHostEnvironment("linux", "arm64");
        const string asset = """{"name":"mihomo-linux-arm64-v1.19.30.gz","browser_download_url":"https://github.com/MetaCubeX/mihomo/releases/download/v1.19.30/mihomo-linux-arm64-v1.19.30.gz"}""";
        var json = "[{\"tag_name\":\"v1.19.30\",\"draft\":false,\"prerelease\":false,\"assets\":[" + asset + "]}]";
        var releases = GitHubComponentReleaseProvider.Parse(json, target, host);
        var selected = new PatternArtifactSelector().Select(releases.Single(), target, host);
        Check(selected.Id == "mihomo-linux-arm64-v1.19.30.gz", "declarative finite template selects server ARM64 asset");
        Reject(() => new PatternArtifactSelector().Select(releases.Single(), target, new("windows", "arm64")), "cross-platform substitution");
        var duplicate = json.Replace(asset, asset + "," + asset);
        Reject(() => new PatternArtifactSelector().Select(GitHubComponentReleaseProvider.Parse(duplicate, target, host).Single(), target, host), "ambiguous assets");
        Reject(() => GitHubComponentReleaseProvider.Parse(json.Replace("https://github.com/MetaCubeX", "https://evil.example/MetaCubeX"), target, host), "asset origin substitution");
        Reject(() => ComponentReleaseRules.Validate(target with { Artifact = target.Artifact with { FileName = "{shell}" } }), "executable/unknown template placeholder");
        Reject(() => new ComponentReleaseProviderRegistry([]).Require("shell"), "missing release provider");
        Reject(() => new ComponentArtifactSelectorRegistry([]).Require("javascript"), "missing selector");
        Reject(() => JsonSerializer.Deserialize<ComponentArtifactRule>("""{"selector":"pattern","script":"whoami"}""",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow }), "selector script field");

        var git = BundledComponentCatalog.Current.Components.Single(component => component.Id == "git");
        var apt = git.Targets.Single(value => value.Os == "linux");
        var package = new ComponentPackageTarget("git", "1:2.53.0-1", "amd64", "ubuntu-resolute", "http://archive.ubuntu.com/ubuntu/",
            "resolute", "main", "http://archive.ubuntu.com/ubuntu/pool/git.deb", new string('1', 64), 123, []);
        var provider = new AptComponentReleaseProvider((id, ct) => Task.FromResult(new ComponentPackageResolution("git", null, package)));
        var aptReleases = await provider.GetReleasesAsync(git, apt, new("linux", "x64"), false, default);
        Check(ReferenceEquals(aptReleases.Single().Package, package), "APT preserves host-frozen repository, version, digest and dependencies");
        Check(new PackageArtifactSelector().Select(aptReleases.Single(), apt, new("linux", "x64")).DownloadUrl is null,
            "APT discovery cannot become a generic archive download");
        try { await provider.GetReleasesAsync(git, apt, new("linux", "arm64"), false, default); throw new Exception("Architecture replacement accepted"); }
        catch (RuntimePolicyException) { }
        if (OperatingSystem.IsLinux())
        {
            var nativePackage = package with { Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
                == System.Runtime.InteropServices.Architecture.X64 ? "amd64" : "arm64" };
            var request = new ComponentPlanRequest("git", "host", RuntimeChange.Install, 17, ComponentSourceMode.SystemPackage,
                null, 0, 0, null, nativePackage.Version, true, "original-plan-key");
            var probe = new PackagePlanProbe(nativePackage);
            var normalized = await new DomainComponentInstallerProvider("apt", ComponentSourceMode.SystemPackage).PlanAsync(probe, request, default);
            Check(normalized.Allowed && ReferenceEquals(normalized.Target!.Package, nativePackage)
                && ReferenceEquals(normalized.Request, request) && probe.Reads == 1,
                "APT installer consumes the same frozen discovery without another latest query or identity replacement");
        }

        Console.WriteLine("Component release provider/selector checks passed.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is RuntimePolicyException or InvalidDataException or JsonException) { return; }
        throw new Exception("Accepted " + message);
    }
    private sealed class PackagePlanProbe(ComponentPackageTarget package) : IComponentLifecycleAdapter
    {
        public int Reads;
        public string ComponentId => "git";
        public IReadOnlyList<ComponentModeSupport> Modes => [new(ComponentSourceMode.SystemPackage, "linux", true, null)];
        public Task<ComponentStateView> StateAsync(CancellationToken ct) => throw new Exception("Unexpected second state read");
        public Task<ComponentPlanResult> PlanAsync(ComponentPlanRequest request, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(new ComponentPlanResult(request, null, ComponentFrozenTarget.FromPackage(package), [], []));
        }
        public Task<ComponentExecutionResult> ExecuteAsync(ComponentExecuteRequest request, CancellationToken ct) =>
            throw new Exception("Discovery must not execute");
    }
}
