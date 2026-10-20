using System.Runtime.InteropServices;

namespace RelaxKonOS.ManagedRuntimes;

/// <summary>What one installed Mihomo binary reported about itself. Not installation or interoperability evidence.</summary>
public sealed record MihomoBuildEvidence(string Version, string Os, string Architecture, string GoVersion,
    IReadOnlySet<string> BuildTags);

/// <summary>Mihomo-specific tag syntax and 'mihomo -v' output. Nothing here is guessed from a file name.</summary>
public static class MihomoNativeValidation
{
    public static string? UpdateProblem(string currentMeasuredVersion, string targetTag)
    {
        if (!RuntimeSemanticVersion.TryParse(currentMeasuredVersion, out var current) || !TryTag(targetTag, out var target))
            return "runtime.native_version_invalid";
        var comparison = target.CompareTo(current);
        return comparison > 0 ? null : comparison == 0 ? "runtime.version_unchanged" : "runtime.update_target_not_newer";
    }

    /// <summary>
    /// Only an explicit 'v' prefix on a strict semantic version is a Mihomo version tag. The upstream
    /// "Prerelease-Alpha" channel and date-stamped builds are not versions and stay unsupported.
    /// </summary>
    public static bool TryTag(string? tag, out RuntimeSemanticVersion version) =>
        RuntimeSemanticVersion.TryParse(tag is { Length: > 1 } && tag[0] == 'v' ? tag[1..] : null, out version);

    /// <summary>
    /// Reads the single fixed 'mihomo -v' report. Format, platform and architecture must all agree with the
    /// signed release; an unrecognized line is rejected rather than trusted, and the report is bounded.
    /// </summary>
    public static MihomoBuildEvidence Parse(string output, string expectedTag, string expectedOs, string expectedArchitecture)
    {
        if (output.Length > 8192 || output.Any(c => char.IsControl(c) && c is not ('\r' or '\n'))
            || !TryTag(expectedTag, out var expected)
            || expectedOs.Length == 0 || expectedArchitecture.Length == 0)
            throw new RuntimePolicyException("runtime.native_version_invalid");
        var lines = output.Split('\n').Select(line => line.EndsWith('\r') ? line[..^1] : line)
            .Where(line => line.Length != 0).ToArray();
        if (lines.Length is 0 or > 2 || lines[0].Length > 512 || !lines[0].StartsWith("Mihomo Meta ", StringComparison.Ordinal))
            throw new RuntimePolicyException("runtime.native_version_invalid");
        var tokens = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 7 || tokens[0] != "Mihomo" || tokens[1] != "Meta" || tokens[5] != "with"
            || !tokens[6].StartsWith("go", StringComparison.Ordinal) || tokens[6].Length < 3
            || !char.IsAsciiDigit(tokens[6][2]))
            throw new RuntimePolicyException("runtime.native_version_invalid");
        if (!TryTag(tokens[2], out var measured) || measured.Normalized != expected.Normalized)
            throw new RuntimePolicyException("runtime.native_version_mismatch");
        if (tokens[3] != expectedOs || tokens[4] != expectedArchitecture)
            throw new RuntimePolicyException("runtime.native_platform_mismatch");
        var tags = new HashSet<string>(StringComparer.Ordinal);
        if (lines.Length == 2)
        {
            if (!lines[1].StartsWith("Use tags: ", StringComparison.Ordinal) || lines[1].Length > 512)
                throw new RuntimePolicyException("runtime.native_version_invalid");
            foreach (var tag in lines[1]["Use tags: ".Length..].Split(','))
                if (tag.Length is 0 or > 64 || !tag.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') || !tags.Add(tag))
                    throw new RuntimePolicyException("runtime.native_features_invalid");
        }
        return new(measured.Normalized, expectedOs, expectedArchitecture, tokens[6], tags);
    }

    /// <summary>Platform name used by the signed catalog for this host, or empty when unsupported.</summary>
    public static string PlatformName => OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsLinux() ? "linux" : "";

    /// <summary>Architecture name used by the signed catalog for this host, or empty when unsupported.</summary>
    public static string ArchitectureName => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        _ => "",
    };

    /// <summary>Fixed executable name; the catalog entrypoint is compared against it, never chosen by a publisher.</summary>
    public static string BinaryName => OperatingSystem.IsWindows() ? "mihomo.exe" : "mihomo";
}
