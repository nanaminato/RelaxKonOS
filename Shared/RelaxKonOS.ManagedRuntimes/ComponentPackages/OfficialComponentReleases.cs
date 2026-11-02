using System.Collections.Concurrent;
using RelaxKonOS.ManagedRuntimes.Catalog;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes.ComponentPackages;

/// <summary>Official release discovery. No custom signing catalog or user-provided URL is required.</summary>
public sealed class OfficialComponentReleases
{
    private static readonly HttpClient Client = CreateClient(allowRedirect: true);
    private static readonly HttpClient MetadataClient = CreateClient(allowRedirect: false);
    private readonly HttpClient metadataClient;
    public OfficialComponentReleases(HttpClient? metadataClient = null) => this.metadataClient = metadataClient ?? MetadataClient;
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, ComponentDownloadRelease[] Releases)> Cache = new();
    private static HttpClient CreateClient(bool allowRedirect)
    {
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = allowRedirect }) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RelaxKonOS/1.0");
        return client;
    }
    public async Task<ComponentDownloadRelease[]> ListAsync(string component, string rid, bool refresh, CancellationToken ct, RuntimeDownloadProxy? proxy = null)
    {
        var key = component + "|" + rid;
        if (!refresh && Cache.TryGetValue(key, out var cached) && cached.At.AddMinutes(15) > DateTimeOffset.UtcNow)
            return cached.Releases;
        var (manifest, target, environment) = CatalogTarget(component, rid);
        var providers = new ComponentReleaseProviderRegistry([
            new GitHubComponentReleaseProvider(ReadAsync, Approve),
            new NginxOfficialReleaseProvider(ReadAsync, Approve),
            new TailscaleReleaseProvider((rid, token) => new TailscaleOfficialReleases().ResolveAsync(rid, null, token, proxy))
        ]);
        var found = await providers.Require(target.Releases.Type).GetReleasesAsync(manifest, target, environment, refresh, ct);
        var selectors = new ComponentArtifactSelectorRegistry([new PatternArtifactSelector(), new NativeArtifactSelector()]);
        var releases = found.Select(release =>
        {
            var artifact = selectors.Require(target.Artifact.Selector).Select(release, target, environment);
            var selected = new ComponentDownloadRelease(release.Version, rid, artifact.DownloadUrl!, artifact.Id, artifact.Format, artifact.Sha256);
            if (component != ComponentIds.Tailscale) VerifyIdentity(selected, Describe(component, rid, release.Version));
            return selected;
        }).OrderByDescending(release => SortVersion(release.Version)).ToArray();
        Cache[key] = (DateTimeOffset.UtcNow, releases);
        return releases;
    }
    public async Task<ComponentDownloadRelease> ResolveAsync(string component, string rid, string version, CancellationToken ct, RuntimeDownloadProxy? proxy = null)
    {
        if (component == ComponentIds.Tailscale) return await new TailscaleOfficialReleases().ResolveAsync(rid,version,ct,proxy);
        var expected = Describe(component, rid, version);
        if (component == ComponentIds.Nginx) return expected;
        var key = component + "|" + rid;
        if (Cache.TryGetValue(key, out var cached) && cached.At.AddMinutes(15) > DateTimeOffset.UtcNow
            && cached.Releases.SingleOrDefault(release => release.Version == version) is { } known) return known;
        var json = await ReadAsync($"https://api.github.com/repos/{Repository(component)}/releases/tags/{version}", ct);
        return ParseGitHub(component, rid, "[" + json + "]").SingleOrDefault(release => release.Version == version)
            ?? throw new RuntimePolicyException("component.package_unresolved");
    }
    public static ComponentDownloadRelease Describe(string component, string rid, string version)
    {
        if (rid is not ("win-x64" or "win-arm64" or "linux-x64" or "linux-arm64")
            || !Regex.IsMatch(version, component == ComponentIds.Nginx ? @"\A[0-9]+\.[0-9]+\.[0-9]+\z" : @"\Av[0-9]+\.[0-9]+\.[0-9]+\z"))
            throw new RuntimePolicyException("component.request_invalid");
        var windows = rid.StartsWith("win-", StringComparison.Ordinal);
        var arch = rid.EndsWith("arm64", StringComparison.Ordinal) ? "arm64" : "amd64";
        var platform = windows ? "windows" : "linux";
        var format = windows ? "zip" : component == ComponentIds.Mihomo ? "gz" : "tar.gz";
        if (component == ComponentIds.Nginx && !windows) throw new RuntimePolicyException("component.platform_unsupported");
        var file = component switch
        {
            ComponentIds.Nginx => $"nginx-{version}.zip",
            ComponentIds.Frpc or ComponentIds.Frps => $"frp_{version[1..]}_{platform}_{arch}.{format}",
            ComponentIds.Mihomo => $"mihomo-{platform}-{arch}-{version}.{format}",
            _ => throw new RuntimePolicyException("component.unknown")
        };
        var url = component == ComponentIds.Nginx ? "https://nginx.org/download/" + file
            : $"https://github.com/{Repository(component)}/releases/download/{version}/{file}";
        return new(version, rid, url, file, format);
    }
    public static ComponentDownloadRelease[] ParseNginx(string html, string rid) =>
        Regex.Matches(html, @"(?:https://nginx\.org)?/download/nginx-([0-9]+\.[0-9]+\.[0-9]+)\.zip(?=[""'])")
            .Select(match => Describe(ComponentIds.Nginx, rid, match.Groups[1].Value)).DistinctBy(release => release.Version)
            .OrderByDescending(release => SortVersion(release.Version)).ToArray();

    public static ComponentDownloadRelease[] ParseGitHub(string component, string rid, string json)
    {
        var (manifest, target, environment) = CatalogTarget(component, rid);
        Approve(manifest, target, environment);
        var selector = new PatternArtifactSelector();
        return GitHubComponentReleaseProvider.Parse(json, target, environment).Select(release =>
        {
            var artifact = selector.Select(release, target, environment);
            var selected = new ComponentDownloadRelease(release.Version, rid, artifact.DownloadUrl!, artifact.Id, artifact.Format, artifact.Sha256);
            VerifyIdentity(selected, Describe(component, rid, release.Version));
            return selected;
        }).ToArray();
    }

    private static (ComponentManifest Manifest, ComponentInstallTarget Target, ComponentHostEnvironment Environment) CatalogTarget(string component, string rid)
    {
        var parts = rid.Split('-');
        if (parts.Length != 2 || parts[0] is not ("win" or "linux") || parts[1] is not ("x64" or "arm64"))
            throw new RuntimePolicyException("component.platform_unsupported");
        var environment = new ComponentHostEnvironment(parts[0] == "win" ? "windows" : "linux", parts[1]);
        var manifest = BundledComponentCatalog.Current.Components.SingleOrDefault(value => value.Id == component)
            ?? throw new RuntimePolicyException("component.unknown");
        var candidates = manifest.Targets.Where(value => value.Os == environment.Os && value.Architectures.Contains(environment.Architecture)
            && value.Installer.Available && value.Installer.Type is "archive" or "download").ToArray();
        if (candidates.Length != 1) throw new RuntimePolicyException("component.release_provider_unavailable");
        return (manifest, candidates[0], environment);
    }

    private static void Approve(ComponentManifest component, ComponentInstallTarget target, ComponentHostEnvironment environment)
    {
        // The local description cannot widen the existing domain publisher/artifact policy.
        // New publishers still require an independent host-owned trust implementation.
        var version = component.Id == ComponentIds.Nginx ? "1.2.3" : "v1.2.3";
        var expected = Describe(component.Id, environment.Rid, version);
        if (target.Os != environment.Os || !target.Architectures.Contains(environment.Architecture)
            || target.Artifact.Selector != "pattern" || target.Artifact.Format != expected.Format
            || PatternArtifactSelector.FileName(target.Artifact, version, environment) != expected.FileName
            || (component.Id == ComponentIds.Nginx ? target.Releases.Type != "nginx-official"
                : target.Releases.Type != "github-releases" || target.Releases.Repository != Repository(component.Id)))
            throw new RuntimePolicyException("component.artifact_source_invalid");
    }

    private static void VerifyIdentity(ComponentDownloadRelease selected, ComponentDownloadRelease expected)
    {
        if (selected.DownloadUrl != expected.DownloadUrl || selected.FileName != expected.FileName || selected.Format != expected.Format || selected.Rid != expected.Rid)
            throw new RuntimePolicyException("component.artifact_source_invalid");
    }
    private static Version SortVersion(string version) => Version.Parse(version.TrimStart('v'));
    public static async Task DownloadAsync(ComponentDownloadRelease release, Stream destination, CancellationToken ct)
    {
        using var response = await Client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await CopyAsync(input, destination, release.Sha256, ct);
    }
    public static async Task CopyAsync(Stream input, Stream destination, string? sha256, CancellationToken ct)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[81920]; long total = 0;
        for (int read; (read = await input.ReadAsync(buffer, ct)) != 0;)
        {
            if ((total += read) > 128L * 1024 * 1024) throw new IOException("Archive too large");
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (total == 0 || !string.IsNullOrEmpty(sha256) && !Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Archive invalid");
    }
    private static string Repository(string component) => component switch
    {
        ComponentIds.Frpc or ComponentIds.Frps => "fatedier/frp",
        ComponentIds.Mihomo => "MetaCubeX/mihomo",
        _ => throw new RuntimePolicyException("component.unknown")
    };
    private async Task<string> ReadAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        ct = timeout.Token;
        using var response = await metadataClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        for (int read; (read = await input.ReadAsync(buffer, ct)) != 0;)
        {
            if (output.Length + read > 8 * 1024 * 1024) throw new IOException("Release metadata too large");
            output.Write(buffer, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }
}
