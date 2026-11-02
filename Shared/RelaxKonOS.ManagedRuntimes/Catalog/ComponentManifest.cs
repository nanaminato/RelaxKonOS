namespace RelaxKonOS.ManagedRuntimes.Catalog;

// These are declarations, never installation authority or executable instructions.
public sealed record ComponentCatalogIndex(int SchemaVersion, IReadOnlyList<ComponentCatalogEntry> Components,
    IReadOnlyList<ComponentWorkload> Workloads);
public sealed record ComponentCatalogEntry(string Id, string Manifest);
public sealed record ComponentWorkload(string WorkloadId, string DisplayNameKey,
    IReadOnlyList<string> RequiredComponents, IReadOnlyList<string> OptionalComponents);
public sealed record ComponentManifest(int SchemaVersion, string Id, string RuntimeId, string Category,
    string Name, string DescriptionKey, string Integration, string? ApplicationId,
    IReadOnlyList<string> RequiredComponents, IReadOnlyList<string> OptionalComponents,
    IReadOnlyList<string> Conflicts, IReadOnlyList<string> Applications, IReadOnlyList<ComponentInstallTarget> Targets);
public sealed record ComponentInstallTarget(string Os, IReadOnlyList<string> Architectures,
    ComponentInstaller Installer, string SourceKind, bool OfficialDownloads,
    ComponentReleaseSource Releases, ComponentArtifactRule Artifact);
public sealed record ComponentInstaller(string Type, bool Available);
public sealed record ComponentReleaseSource(string Type, string? Repository = null);
public sealed record ComponentArtifactRule(string Selector, string? FileName = null, string? Format = null,
    string? Reference = null, string? Sha256 = null, string? Version = null);
public sealed record ResolvedComponentCatalog(IReadOnlyList<ComponentManifest> Components,
    IReadOnlyList<ComponentWorkload> Workloads);

public enum ComponentCompatibility { Supported, UnsupportedPlatform, ProviderUnavailable, PolicyBlocked, Unknown }
public sealed record ComponentCompatibilityResult(ComponentCompatibility Status, ComponentInstallTarget? Target);

public static class CompatibilityResolver
{
    /// <summary>Host facts only; never substitute the desktop's architecture for a remote host.</summary>
    public static ComponentCompatibilityResult Resolve(ComponentManifest component, string? os, string? architecture,
        Func<ComponentInstallTarget, bool> providerAvailable, bool policyAllowed = true)
    {
        if (string.IsNullOrEmpty(os) || string.IsNullOrEmpty(architecture) || os == "unknown" || architecture == "unknown")
            return new(ComponentCompatibility.Unknown, null);
        var targets = component.Targets.Where(target => target.Os == os && target.Architectures.Contains(architecture)).ToArray();
        if (targets.Length == 0) return new(ComponentCompatibility.UnsupportedPlatform, null);
        if (!policyAllowed) return new(ComponentCompatibility.PolicyBlocked, null);
        var target = targets.FirstOrDefault(target => target.Installer.Available && providerAvailable(target));
        return target is null ? new(ComponentCompatibility.ProviderUnavailable, null) : new(ComponentCompatibility.Supported, target);
    }
}
