namespace RelaxKonOS.ManagedRuntimes;

/// <summary>
/// The one declared native-format range for Mihomo that this build may install and manage. Discovery stays
/// broader: a catalog may list any upstream release, but one outside this range is only ever shown as
/// unsupported. The range describes the configuration this build generates, so no catalog signature can
/// widen it and no upstream release can claim it.
/// </summary>
public static class MihomoRuntimeSupport
{
    /// <summary>Name of the current generated native format (Mihomo YAML profile plus Server-owned keys).</summary>
    public const string Capability = "mihomo-v1";

    /// <summary>First stable release whose native schema accepts the current generated configuration.</summary>
    private static readonly RuntimeSemanticVersion Minimum = Parse("1.19.0");

    /// <summary>Exclusive upper bound; the next format change opens a new capability instead of extending this one.</summary>
    private static readonly RuntimeSemanticVersion Limit = Parse("1.20.0");

    /// <summary>
    /// Stable releases inside the measured range. Prereleases are excluded rather than guessed, and the
    /// upstream "Prerelease-Alpha" channel is not a version at all, so it never resolves to a capability.
    /// </summary>
    public static bool Supports(RuntimeSemanticVersion version) =>
        !version.IsPrerelease && version.CompareTo(Minimum) >= 0 && version.CompareTo(Limit) < 0;

    public static string? CapabilityFor(string? normalizedVersion) =>
        RuntimeSemanticVersion.TryParse(normalizedVersion, out var version) && Supports(version) ? Capability : null;

    private static RuntimeSemanticVersion Parse(string value) =>
        RuntimeSemanticVersion.TryParse(value, out var version) ? version : throw new InvalidOperationException(value);
}
