using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes.Catalog;

public sealed record ComponentHostEnvironment(string Os, string Architecture)
{
    public string Rid => (Os == "windows" ? "win" : Os) + "-" + Architecture;
}
public sealed record ComponentArtifact(string Id, string Os, string Architecture, string Format,
    string? DownloadUrl, string? Reference, string? Sha256);
public sealed record ComponentRelease(string ReleaseId, string Version, string Channel,
    IReadOnlyList<ComponentArtifact> Artifacts, ComponentPackageTarget? Package = null);
public interface IComponentReleaseProvider
{
    string Type { get; }
    Task<IReadOnlyList<ComponentRelease>> GetReleasesAsync(ComponentManifest component, ComponentInstallTarget target,
        ComponentHostEnvironment environment, bool refresh, CancellationToken ct);
}
public interface IComponentArtifactSelector
{
    string Type { get; }
    ComponentArtifact Select(ComponentRelease release, ComponentInstallTarget target, ComponentHostEnvironment environment);
}
public sealed class ComponentReleaseProviderRegistry(IEnumerable<IComponentReleaseProvider> providers)
{
    private readonly IReadOnlyDictionary<string, IComponentReleaseProvider> _providers =
        providers.ToDictionary(provider => provider.Type, StringComparer.Ordinal);
    public IComponentReleaseProvider Require(string type) => _providers.GetValueOrDefault(type)
        ?? throw new RuntimePolicyException("component.release_provider_unavailable");
}
public sealed class ComponentArtifactSelectorRegistry(IEnumerable<IComponentArtifactSelector> selectors)
{
    private readonly IReadOnlyDictionary<string, IComponentArtifactSelector> _selectors =
        selectors.ToDictionary(selector => selector.Type, StringComparer.Ordinal);
    public IComponentArtifactSelector Require(string type) => _selectors.GetValueOrDefault(type)
        ?? throw new RuntimePolicyException("component.artifact_selector_unavailable");
}

/// <summary>A finite filename template, never a regular expression or executable parser.</summary>
public sealed class PatternArtifactSelector : IComponentArtifactSelector
{
    public string Type => "pattern";
    public static string FileName(ComponentArtifactRule rule, string version, ComponentHostEnvironment environment)
    {
        if (!Regex.IsMatch(version, @"\Av?[0-9]+\.[0-9]+\.[0-9]+\z"))
            throw new RuntimePolicyException("component.request_invalid");
        var name = (rule.FileName ?? "").Replace("{version}", version, StringComparison.Ordinal)
            .Replace("{versionNumber}", version.TrimStart('v'), StringComparison.Ordinal)
            .Replace("{os}", environment.Os, StringComparison.Ordinal)
            .Replace("{arch}", environment.Architecture == "x64" ? "amd64" : environment.Architecture, StringComparison.Ordinal);
        if (!Regex.IsMatch(name, @"\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,199}\z"))
            throw new RuntimePolicyException("component.artifact_invalid");
        return name;
    }
    public ComponentArtifact Select(ComponentRelease release, ComponentInstallTarget target, ComponentHostEnvironment environment)
    {
        var name = FileName(target.Artifact, release.Version, environment);
        var candidates = release.Artifacts.Where(artifact => artifact.Id == name && artifact.Os == environment.Os
            && artifact.Architecture == environment.Architecture && artifact.Format == target.Artifact.Format).ToArray();
        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new RuntimePolicyException("component.package_unresolved"),
            _ => throw new RuntimePolicyException("component.artifact_ambiguous")
        };
    }
}

public static class ComponentReleaseRules
{
    public static void Validate(ComponentInstallTarget target)
    {
        var source = target.Releases;
        var rule = target.Artifact;
        if (source.Repository is not null && (!Regex.IsMatch(source.Repository, @"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z")
                || source.Type != "github-releases")) throw Invalid();
        if (source.Type == "github-releases" && source.Repository is null) throw Invalid();
        if (rule.Sha256 is not null && !Regex.IsMatch(rule.Sha256, @"\A[0-9a-f]{64}\z")) throw Invalid();
        if (rule.Selector == "pattern")
        {
            if (source.Type is not ("github-releases" or "nginx-official") || target.Installer.Type != "archive"
                || rule.Reference is not null || rule.Version is not null || rule.FileName is null
                || rule.Format is not ("zip" or "gz" or "tar.gz")) throw Invalid();
            var stripped = rule.FileName.Replace("{version}", "1.2.3").Replace("{versionNumber}", "1.2.3")
                .Replace("{os}", "linux").Replace("{arch}", "amd64");
            if (!Regex.IsMatch(stripped, @"\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,199}\z")) throw Invalid();
        }
        else if (rule.Selector == "catalog-artifact")
        {
            if (source.Type is not ("catalog" or "static") || target.Installer.Type != "roapp" || rule.Format != "roapp"
                || rule.Reference is null || rule.Sha256 is null || string.IsNullOrWhiteSpace(rule.Version) || rule.FileName is not null)
                throw Invalid();
        }
        else if (rule.Selector == "package")
        {
            if (source.Type != "apt" || target.Installer.Type != "apt" || rule != new ComponentArtifactRule("package")) throw Invalid();
        }
        else if (rule.Selector == "native")
        {
            if (source.Type != "tailscale-official" || target.Installer.Type != "download" || rule != new ComponentArtifactRule("native", Format: "msi")) throw Invalid();
        }
        else if (rule.Selector != "unavailable" || target.Installer.Available || source.Type != "unavailable"
            || rule != new ComponentArtifactRule("unavailable")) throw Invalid();
    }
    private static InvalidDataException Invalid() => new("component.catalog_invalid");
}

public sealed class LocalComponentReleaseProvider(string type = "catalog") : IComponentReleaseProvider
{
    public string Type { get; } = type;
    public Task<IReadOnlyList<ComponentRelease>> GetReleasesAsync(ComponentManifest component, ComponentInstallTarget target,
        ComponentHostEnvironment environment, bool refresh, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ComponentReleaseRules.Validate(target);
        if (target.Releases.Type != Type || target.Artifact.Selector != "catalog-artifact"
            || target.Os != environment.Os || !target.Architectures.Contains(environment.Architecture))
            throw new RuntimePolicyException("component.platform_unsupported");
        var rule = target.Artifact;
        return Task.FromResult<IReadOnlyList<ComponentRelease>>([new(rule.Version!, rule.Version!, "stable",
            [new(component.Id, environment.Os, environment.Architecture, "roapp", null, rule.Reference, rule.Sha256)])]);
    }
}

public sealed class CatalogArtifactSelector : IComponentArtifactSelector
{
    public string Type => "catalog-artifact";
    public ComponentArtifact Select(ComponentRelease release, ComponentInstallTarget target, ComponentHostEnvironment environment)
    {
        var candidates = release.Artifacts.Where(artifact => artifact.Os == environment.Os && artifact.Architecture == environment.Architecture
            && artifact.Reference == target.Artifact.Reference && artifact.Sha256 == target.Artifact.Sha256 && artifact.Format == "roapp").ToArray();
        if (release.Version != target.Artifact.Version || candidates.Length != 1)
            throw new RuntimePolicyException("component.artifact_ambiguous");
        return candidates[0];
    }
}
