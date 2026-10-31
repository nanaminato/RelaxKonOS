using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes.ComponentPackages;

/// <summary>Official release discovery. No custom signing catalog or user-provided URL is required.</summary>
public sealed class OfficialComponentReleases
{
    private static readonly HttpClient Client = CreateClient();
    private readonly HttpClient metadataClient;
    public OfficialComponentReleases(HttpClient? metadataClient = null) => this.metadataClient = metadataClient ?? Client;
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, ComponentDownloadRelease[] Releases)> Cache = new();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RelaxKonOS/1.0");
        return client;
    }
    public async Task<ComponentDownloadRelease[]> ListAsync(string component, string rid, bool refresh, CancellationToken ct, RuntimeDownloadProxy? proxy = null)
    {
        var key = component + "|" + rid;
        if (!refresh && Cache.TryGetValue(key, out var cached) && cached.At.AddMinutes(15) > DateTimeOffset.UtcNow)
            return cached.Releases;
        ComponentDownloadRelease[] releases;
        if (component == ComponentIds.Tailscale)
            releases = [await new TailscaleOfficialReleases().ResolveAsync(rid,null,ct,proxy)];
        else if (component == ComponentIds.Nginx)
        {
            if (!rid.StartsWith("win-", StringComparison.Ordinal)) return [];
            releases = ParseNginx(await ReadAsync("https://nginx.org/en/download.html", ct), rid);
        }
        else
        {
            var repository = Repository(component);
            var found = new List<ComponentDownloadRelease>();
            for (var page = 1; page <= 3; page++)
            {
                var json = await ReadAsync($"https://api.github.com/repos/{repository}/releases?per_page=5&page={page}", ct);
                found.AddRange(ParseGitHub(component, rid, json));
                using var document = JsonDocument.Parse(json);
                // Return the newest matching batch without downloading historical asset metadata.
                if (found.Count > 0 || document.RootElement.GetArrayLength() < 5) break;
            }
            releases = found.DistinctBy(release => release.Version).OrderByDescending(release => SortVersion(release.Version)).ToArray();
        }
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
        using var document = JsonDocument.Parse(json);
        var results = new List<ComponentDownloadRelease>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.GetProperty("draft").GetBoolean() || item.GetProperty("prerelease").GetBoolean()) continue;
            var tag = item.GetProperty("tag_name").GetString() ?? "";
            if (!Regex.IsMatch(tag, @"\Av[0-9]+\.[0-9]+\.[0-9]+\z")) continue;
            var expected = Describe(component, rid, tag);
            foreach (var asset in item.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != expected.FileName
                    || asset.GetProperty("browser_download_url").GetString() != expected.DownloadUrl) continue;
                var digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() : null;
                results.Add(expected with { Sha256 = digest is not null && Regex.IsMatch(digest, @"\Asha256:[0-9a-fA-F]{64}\z") ? digest[7..].ToLowerInvariant() : null });
            }
        }
        return results.ToArray();
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
