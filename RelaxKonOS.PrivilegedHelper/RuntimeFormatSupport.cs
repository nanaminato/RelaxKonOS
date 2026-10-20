using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// The single place that decides what a catalog entry may claim about native configuration support.
/// A publisher only ever supplies a version tag; the capability comes from this build's measured
/// range, and the same decision is reused by the install-time adapter of the same runtime.
/// </summary>
internal static class RuntimeFormatSupport
{
    internal const string SingBox = "sing-box";
    internal const string Mihomo = "mihomo";

    /// <summary>Runtimes whose upstream version tags sort by semantic version rather than by date or revision.</summary>
    internal static bool SortsBySemanticVersion(string? runtimeId) => runtimeId is SingBox or Mihomo;

    internal static RuntimeConfigurationSupport Configuration(ManifestRelease release)
    {
        RuntimeSemanticVersion version;
        switch (release.RuntimeId)
        {
            case SingBox when SingBoxNativeValidation.TryTag(release.OriginalVersion, out var singBox):
                version = singBox;
                break;
            case Mihomo when MihomoNativeValidation.TryTag(release.OriginalVersion, out var mihomo):
                version = mihomo;
                break;
            // Unknown naming is shown as unsupported instead of being coerced into this runtime's scheme.
            default:
                return new("", "", false);
        }
        // A prerelease in a stable channel (or the reverse) is a publisher error, not a version choice.
        if (version.IsPrerelease != (release.Channel == RuntimeChannel.Prerelease))
            throw new RuntimePolicyException("runtime.release_channel_invalid");
        var capability = release.RuntimeId == SingBox
            ? SingBoxRuntimeSupport.CapabilityFor(version.Normalized)
            : MihomoRuntimeSupport.CapabilityFor(version.Normalized);
        return new(version.Normalized, capability ?? "", capability is not null);
    }
}
