using System.Collections.Immutable;
using RelaxKonOS.ManagedRuntimes;

namespace RelaxKonOS.Deployment;

public enum DeploymentPackageKind { Application, ServerModule }

public readonly record struct DeploymentPackageKey(DeploymentPackageKind Kind, string Id);

public sealed record DeploymentRequirement(DeploymentPackageKey Package,
    string? MinimumVersion = null, string? MaximumVersionExclusive = null,
    string? Capability = null, string? ContractVersion = null);

public sealed record DeploymentRelease(DeploymentPackageKey Package, string Version, string ArtifactIdentity,
    bool Satisfied, ImmutableArray<DeploymentRequirement> Requires,
    ImmutableDictionary<string, string> Capabilities)
{
    public string? CurrentVersion { get; init; }
}

public sealed record DeploymentResolution(ImmutableArray<DeploymentRelease> OrderedReleases, string? Problem,
    DeploymentPackageKey? ProblemPackage = null)
{
    public bool Succeeded => Problem is null;
}

public sealed class DeploymentResolutionException(string problem, DeploymentPackageKey? package)
    : InvalidOperationException(problem)
{
    public DeploymentPackageKey? Package { get; } = package;
}

/// <summary>Resolves a snapshot of admitted releases. The caller supplies platform-compatible candidates.</summary>
public static class DeploymentResolver
{
    private const int MaximumSearchSteps = 16_384;
    private const int MaximumDepth = 128;

    public static DeploymentResolution Resolve(DeploymentRelease root,
        IEnumerable<DeploymentRelease> candidates, IEnumerable<DeploymentRequirement> selectedOptional)
    {
        var releases = candidates
            .Select(release => (Release: release, Version: Parse(release.Version)))
            .ToLookup(entry => entry.Release.Package);
        var selected = new Dictionary<DeploymentPackageKey, DeploymentRelease> { [root.Package] = root };
        var requirements = root.Requires.AddRange(selectedOptional);
        var pinnedRoot = root with { Requires = requirements };
        selected[root.Package] = pinnedRoot;
        var remaining = MaximumSearchSteps;
        var problem = "deployment-dependency-conflict";
        DeploymentPackageKey? problemPackage = null;
        ImmutableArray<DeploymentRelease> order = [];

        bool Search(ImmutableArray<DeploymentRequirement> pending, int index, int depth)
        {
            if (remaining-- <= 0 || depth > MaximumDepth)
            {
                problem = "deployment-resolution-limit";
                return false;
            }

            while (index < pending.Length && selected.TryGetValue(pending[index].Package, out var existing))
            {
                if (!Matches(existing, pending[index]))
                {
                    problemPackage = pending[index].Package;
                    return false;
                }
                index++;
            }

            if (index == pending.Length)
            {
                var ordered = ImmutableArray.CreateBuilder<DeploymentRelease>();
                var visiting = new HashSet<DeploymentPackageKey>();
                var complete = new HashSet<DeploymentPackageKey>();

                bool Visit(DeploymentRelease release)
                {
                    if (complete.Contains(release.Package)) return true;
                    if (!visiting.Add(release.Package))
                    {
                        problemPackage = release.Package;
                        return false;
                    }
                    foreach (var dependency in release.Requires)
                        if (!Visit(selected[dependency.Package])) return false;
                    visiting.Remove(release.Package);
                    complete.Add(release.Package);
                    ordered.Add(release);
                    return true;
                }

                if (!Visit(pinnedRoot))
                {
                    problem = "deployment-dependency-cycle";
                    return false;
                }

                order = ordered.ToImmutable();
                return true;
            }

            var requirement = pending[index];
            problemPackage = requirement.Package;
            foreach (var entry in releases[requirement.Package].Where(entry => Matches(entry.Release, requirement))
                .OrderByDescending(entry => entry.Release.Satisfied)
                .ThenByDescending(entry => entry.Version)
                .ThenBy(entry => entry.Release.ArtifactIdentity, StringComparer.Ordinal))
            {
                selected.Add(requirement.Package, entry.Release);
                if (Search(pending.AddRange(entry.Release.Requires), index + 1, depth + 1)) return true;
                selected.Remove(requirement.Package);
                if (remaining <= 0) break;
            }
            return false;
        }

        return Search(requirements, 0, 0) ? new(order, null) : new([], problem, problemPackage);
    }

    private static bool Matches(DeploymentRelease release, DeploymentRequirement requirement)
    {
        return IsVersionAllowed(release.Version, requirement)
            && (requirement.Capability is null
                ? requirement.ContractVersion is null
                : release.Capabilities.TryGetValue(requirement.Capability, out var actual)
                    && (requirement.ContractVersion is null || actual == requirement.ContractVersion));
    }

    public static bool IsVersionAllowed(string version, DeploymentRequirement requirement) =>
        RuntimeSemanticVersion.TryParse(version, out var parsed)
        && (requirement.MinimumVersion is null || parsed.CompareTo(Parse(requirement.MinimumVersion)) >= 0)
        && (requirement.MaximumVersionExclusive is null || parsed.CompareTo(Parse(requirement.MaximumVersionExclusive)) < 0);

    private static RuntimeSemanticVersion Parse(string version) =>
        RuntimeSemanticVersion.TryParse(version, out var parsed)
            ? parsed : throw new InvalidDataException("Invalid deployment version.");
}
