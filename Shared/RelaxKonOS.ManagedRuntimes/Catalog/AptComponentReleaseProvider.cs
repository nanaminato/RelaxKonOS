using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes.Catalog;

/// <summary>Normalizes the existing host-owned APT resolution; never executes apt or accepts package names from JSON.</summary>
public sealed class AptComponentReleaseProvider(Func<string, CancellationToken, Task<ComponentPackageResolution>> resolve) : IComponentReleaseProvider
{
    public string Type => "apt";
    public async Task<IReadOnlyList<ComponentRelease>> GetReleasesAsync(ComponentManifest component, ComponentInstallTarget target,
        ComponentHostEnvironment environment, bool refresh, CancellationToken ct)
    {
        if (target.Releases.Type != Type || target.Artifact.Selector != "package" || environment.Os != "linux"
            || target.Os != environment.Os || !target.Architectures.Contains(environment.Architecture))
            throw new RuntimePolicyException("component.platform_unsupported");
        var definition = HostPackageDefinitions.Find(component.Id) ?? throw new RuntimePolicyException("component.release_provider_unavailable");
        var observed = await resolve(component.Id, ct);
        if (observed.BlockingProblem is { } problem) throw new RuntimePolicyException(problem);
        if (observed.Candidate is not { } package) return [];
        var architecture = environment.Architecture == "x64" ? "amd64" : environment.Architecture;
        if (observed.PackageName != definition.PackageName || package.PackageName != definition.PackageName || package.Architecture != architecture)
            throw new RuntimePolicyException("component.artifact_source_invalid");
        return [new(package.RepositoryId + ":" + package.Version, package.Version, "stable",
            [new(package.PackageName, environment.Os, environment.Architecture, "deb", null, null, package.Sha256)], package)];
    }
}

public sealed class PackageArtifactSelector : IComponentArtifactSelector
{
    public string Type => "package";
    public ComponentArtifact Select(ComponentRelease release, ComponentInstallTarget target, ComponentHostEnvironment environment)
    {
        if (target.Artifact.Selector != Type || release.Package is not { } package || release.Version != package.Version)
            throw new RuntimePolicyException("component.package_expectation_changed");
        var candidates = release.Artifacts.Where(artifact => artifact.Id == package.PackageName && artifact.Os == environment.Os
            && artifact.Architecture == environment.Architecture && artifact.Format == "deb" && artifact.Sha256 == package.Sha256).ToArray();
        return candidates.Length == 1 ? candidates[0] : throw new RuntimePolicyException("component.artifact_ambiguous");
    }
}
