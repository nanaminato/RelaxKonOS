using System.Text.Json;
using System.Text.RegularExpressions;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;

namespace RelaxKonOS.ManagedRuntimes.Catalog;

/// <summary>The caller supplies a host-owned source approval check, never a manifest public key.</summary>
public sealed class GitHubComponentReleaseProvider(Func<string, CancellationToken, Task<string>> readMetadata,
    Action<ComponentManifest, ComponentInstallTarget, ComponentHostEnvironment> approve) : IComponentReleaseProvider
{
    public string Type => "github-releases";
    public async Task<IReadOnlyList<ComponentRelease>> GetReleasesAsync(ComponentManifest component, ComponentInstallTarget target,
        ComponentHostEnvironment environment, bool refresh, CancellationToken ct)
    {
        approve(component, target, environment);
        for (var page = 1; page <= 3; page++)
        {
            var json = await readMetadata($"https://api.github.com/repos/{target.Releases.Repository}/releases?per_page=5&page={page}", ct);
            var releases = Parse(json, target, environment);
            using var document = JsonDocument.Parse(json);
            if (releases.Any(release => HasTarget(release, target, environment)) || document.RootElement.GetArrayLength() < 5)
                return releases;
        }
        return [];
    }

    private static bool HasTarget(ComponentRelease release, ComponentInstallTarget target, ComponentHostEnvironment environment) =>
        release.Artifacts.Any(artifact => artifact.Id == PatternArtifactSelector.FileName(target.Artifact, release.Version, environment));

    public static IReadOnlyList<ComponentRelease> Parse(string json, ComponentInstallTarget target, ComponentHostEnvironment environment)
    {
        ComponentReleaseRules.Validate(target);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var releases = new List<ComponentRelease>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.GetProperty("draft").GetBoolean() || item.GetProperty("prerelease").GetBoolean()) continue;
            var version = item.GetProperty("tag_name").GetString() ?? "";
            if (!Regex.IsMatch(version, @"\Av[0-9]+\.[0-9]+\.[0-9]+\z")) continue;
            var artifacts = new List<ComponentArtifact>();
            var prefix = $"https://github.com/{target.Releases.Repository}/releases/download/{version}/";
            foreach (var asset in item.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                var uri = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (!Regex.IsMatch(name, @"\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,199}\z") || uri != prefix + name)
                {
                    if (name == PatternArtifactSelector.FileName(target.Artifact, version, environment))
                        throw new RuntimePolicyException("component.artifact_source_invalid");
                    continue;
                }
                var digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() : null;
                artifacts.Add(new(name, environment.Os, environment.Architecture, target.Artifact.Format!, uri, null,
                    digest is not null && Regex.IsMatch(digest, @"\Asha256:[0-9a-fA-F]{64}\z") ? digest[7..].ToLowerInvariant() : null));
            }
            if (artifacts.Any(artifact => artifact.Id == PatternArtifactSelector.FileName(target.Artifact, version, environment)))
                releases.Add(new(version, version, "stable", artifacts.AsReadOnly()));
        }
        if (releases.Select(release => release.ReleaseId).Distinct(StringComparer.Ordinal).Count() != releases.Count)
            throw new RuntimePolicyException("component.artifact_ambiguous");
        return releases.AsReadOnly();
    }
}

public sealed class NginxOfficialReleaseProvider(Func<string, CancellationToken, Task<string>> readMetadata,
    Action<ComponentManifest, ComponentInstallTarget, ComponentHostEnvironment> approve) : IComponentReleaseProvider
{
    public string Type => "nginx-official";
    public async Task<IReadOnlyList<ComponentRelease>> GetReleasesAsync(ComponentManifest component, ComponentInstallTarget target,
        ComponentHostEnvironment environment, bool refresh, CancellationToken ct)
    {
        approve(component, target, environment);
        var html = await readMetadata("https://nginx.org/en/download.html", ct);
        return OfficialComponentReleases.ParseNginx(html, environment.Rid).Select(release => new ComponentRelease(
            release.Version, release.Version, "stable", [new(release.FileName, environment.Os, environment.Architecture,
                release.Format, release.DownloadUrl, null, release.Sha256)])).ToArray();
    }
}

public sealed class TailscaleReleaseProvider(Func<string, CancellationToken, Task<RelaxKonOS.Protocol.ManagedRuntimes.ComponentDownloadRelease>> resolve)
    : IComponentReleaseProvider
{
    public string Type => "tailscale-official";
    public async Task<IReadOnlyList<ComponentRelease>> GetReleasesAsync(ComponentManifest component, ComponentInstallTarget target,
        ComponentHostEnvironment environment, bool refresh, CancellationToken ct)
    {
        if (component.Id != "tailscale" || target.Artifact.Selector != "native" || environment.Os != "windows")
            throw new RuntimePolicyException("component.artifact_source_invalid");
        var release = await resolve(environment.Rid, ct);
        return [new(release.Version, release.Version, "stable", [new(release.FileName, environment.Os, environment.Architecture,
            release.Format, release.DownloadUrl, null, release.Sha256)])];
    }
}

public sealed class NativeArtifactSelector : IComponentArtifactSelector
{
    public string Type => "native";
    public ComponentArtifact Select(ComponentRelease release, ComponentInstallTarget target, ComponentHostEnvironment environment)
    {
        var candidates = release.Artifacts.Where(artifact => artifact.Os == environment.Os && artifact.Architecture == environment.Architecture
            && artifact.Format == target.Artifact.Format).ToArray();
        return candidates.Length == 1 ? candidates[0] : throw new RuntimePolicyException("component.artifact_ambiguous");
    }
}
