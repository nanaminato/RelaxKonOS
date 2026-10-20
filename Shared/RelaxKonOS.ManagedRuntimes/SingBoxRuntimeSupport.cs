namespace RelaxKonOS.ManagedRuntimes;

/// <summary>
/// The one declared native-format range that this build may install and manage. Version discovery stays
/// broader: a catalog may list any upstream release, but a release outside this range is only ever shown
/// as unsupported. The range is a statement about the generator, not about the publisher, so a catalog
/// signature can never widen it.
/// </summary>
public static class SingBoxRuntimeSupport
{
    /// <summary>Name of the current generated native format. Older configuration shapes are not emulated.</summary>
    public const string Capability = "sing-box-v1";

    /// <summary>First stable release whose native schema accepts the current generated configuration.</summary>
    private static readonly RuntimeSemanticVersion Minimum = Parse("1.13.0");

    /// <summary>Exclusive upper bound; the next format change opens a new capability instead of extending this one.</summary>
    private static readonly RuntimeSemanticVersion Limit = Parse("1.15.0");

    /// <summary>
    /// Stable releases inside the measured range. Prereleases are excluded rather than guessed: no
    /// prerelease has configuration or interoperability evidence yet.
    /// </summary>
    public static bool Supports(RuntimeSemanticVersion version) =>
        !version.IsPrerelease && version.CompareTo(Minimum) >= 0 && version.CompareTo(Limit) < 0;

    public static string? CapabilityFor(string? normalizedVersion) =>
        RuntimeSemanticVersion.TryParse(normalizedVersion, out var version) && Supports(version) ? Capability : null;

    private static RuntimeSemanticVersion Parse(string value) =>
        RuntimeSemanticVersion.TryParse(value, out var version) ? version : throw new InvalidOperationException(value);
}
