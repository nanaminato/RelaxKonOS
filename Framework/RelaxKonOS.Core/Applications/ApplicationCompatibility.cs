namespace RelaxKonOS.Core.Applications;

/// <summary>Result of a host-owned compatibility check performed before an application is activated.</summary>
public sealed record ApplicationCompatibilityResult(
    ApplicationCompatibilityStatus Status,
    string? Expected = null,
    string? Actual = null)
{
    public bool IsCompatible => Status == ApplicationCompatibilityStatus.Compatible;
    public static readonly ApplicationCompatibilityResult Compatible = new(ApplicationCompatibilityStatus.Compatible);
}

public enum ApplicationCompatibilityStatus
{
    Compatible,
    ClientPlatformMismatch,
    ServerPlatformMismatch,
    MissingServerCapability,
    ServerUnavailable,
}

/// <summary>
/// Implemented by the shell. The runtime consults it for every launch and file-open request,
/// so package declarations are enforced before third-party code is loaded.
/// </summary>
public interface IApplicationCompatibilityEvaluator
{
    ApplicationCompatibilityResult Evaluate(ApplicationManifest manifest);
}

/// <summary>Lets the shell present an incompatibility as a host-owned desktop window.</summary>
public interface IApplicationCompatibilityNotifier
{
    void Notify(ApplicationManifest manifest, ApplicationCompatibilityResult result);
}

/// <summary>Server-side requirements declared by an application package; empty values mean unrestricted.</summary>
public sealed record ApplicationServerRequirements(
    IReadOnlyList<string>? Platforms = null,
    IReadOnlyList<string>? Capabilities = null)
{
    public IReadOnlyList<string> SupportedPlatforms => ApplicationPlatformNames.Normalize(Platforms);
    public IReadOnlyList<string> RequiredCapabilities => (Capabilities ?? Array.Empty<string>())
        .Where(capability => !string.IsNullOrWhiteSpace(capability))
        .Select(capability => capability.Trim())
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}

/// <summary>Canonical platform names shared by manifests and host evaluators.</summary>
public static class ApplicationPlatformNames
{
    public const string Windows = "windows";
    public const string Linux = "linux";

    public static IReadOnlyList<string> Normalize(IReadOnlyList<string>? values) => (values ?? Array.Empty<string>())
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim().ToLowerInvariant())
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}
