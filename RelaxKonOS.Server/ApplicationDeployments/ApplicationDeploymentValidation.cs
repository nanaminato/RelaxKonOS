using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Server.ApplicationDeployments;

/// <summary>
/// Shared validation and naming for the application deployment domain. Request validation never
/// accepts a host path, a shell fragment, or an unmanaged Docker resource name.
/// </summary>
internal static class ApplicationDeploymentValidation
{
    /// <summary>Label prefix that marks every managed Docker resource and its owner.</summary>
    public const string LabelPrefix = "relaxkonos.";
    public const string ManagedLabel = "relaxkonos.managed";
    public const string OwnerLabel = "relaxkonos.owner";
    public const string OwnerValue = "application-deployment";
    public const string ApplicationIdLabel = "relaxkonos.application-id";
    public const string ApplicationLabel = "relaxkonos.application";
    public const string RevisionIdLabel = "relaxkonos.revision-id";
    public const string RevisionLabel = "relaxkonos.revision";
    public const string OperationLabel = "relaxkonos.operation-id";
    public const string RoleLabel = "relaxkonos.role";
    public const string RoleWorkload = "workload";
    public const string RoleCandidate = "candidate";

    public static readonly IReadOnlyList<string> SupportedBindAddresses = ["127.0.0.1", "0.0.0.0", "::1"];
    public static readonly IReadOnlyList<string> SupportedPlatforms = ["linux/amd64", "linux/arm64", "linux/arm"];

    public static string Reference(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>An application name is also the Docker resource stem, so it is deliberately narrow.</summary>
    public static bool IsValidName(string? value) => value is { Length: >= 3 and <= 40 }
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '-' or '_');

    public static bool IsValidVolumeName(string? value) => value is { Length: >= 1 and <= 32 }
        && char.IsAsciiLetterLower(value[0])
        && value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '-' or '_');

    public static bool IsValidEnvironmentName(string? value) => value is { Length: >= 1 and <= 64 }
        && (char.IsAsciiLetter(value[0]) || value[0] == '_')
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    /// <summary>An absolute container path with no traversal segment and no control characters.</summary>
    public static bool IsValidContainerPath(string? value) => value is { Length: >= 2 and <= 256 }
        && value[0] == '/'
        && !value.Any(char.IsControl)
        && !value.Split('/').Any(segment => segment is "." or "..");

    public static bool IsValidHealthPath(string? value) => value is null
        || value is { Length: >= 1 and <= 256 } && value[0] == '/' && !value.Any(char.IsControl);

    public static bool IsValidBindAddress(string? value) => value is not null && SupportedBindAddresses.Contains(value, StringComparer.Ordinal);

    public static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    public static bool IsValidLimits(ApplicationResourceLimitsDto? limits) => limits is null
        || (limits.CpuCores is null or (> 0 and <= 64))
        && (limits.MemoryBytes is null or (>= 16L * 1024 * 1024 and <= 64L * 1024 * 1024 * 1024))
        && (limits.PidsLimit is null or (> 0 and <= 65535));

    public static bool IsValidVolumes(IReadOnlyList<ApplicationVolumeRecord>? volumes) => volumes is null
        || volumes.Count <= 8
        && volumes.All(x => IsValidVolumeName(x.Name) && IsValidContainerPath(x.ContainerPath))
        && volumes.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() == volumes.Count;

    public static bool IsValidConfiguration(IReadOnlyList<ApplicationConfigRecord>? configuration) => configuration is null
        || configuration.Count <= 64
        && configuration.All(x => IsValidEnvironmentName(x.Name) && x.SecretVersion >= 0
            && (x.IsSecret ? x.Value is null && x.SecretVersion >= 1 : x.Value is { Length: <= 4096 }))
        && configuration.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() == configuration.Count;

    public static bool IsValidProblemCode(string? code) => code is null
        || code.Length <= 120 && code.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    public static bool IsValidReference(string? value, int length) => value is not null && value.Length == length && value.All(char.IsAsciiHexDigitUpper);

    /// <summary>A Docker resource name derived from an application id; it is stable across revisions.</summary>
    public static string ContainerName(Guid applicationId)
    {
        var suffix = applicationId.ToString("N")[..12];
        return $"relaxkonos-ad-{suffix}";
    }

    public static string CandidateContainerName(Guid applicationId) => ContainerName(applicationId) + "-cand";

    public static string VolumeName(Guid applicationId, string volumeName) => $"{ContainerName(applicationId)}-{volumeName}";

    /// <summary>
    /// Locally built images are tagged with the deployment input, so the same input always produces
    /// the same tag and a different input can never silently reuse the previous build.
    /// </summary>
    public static string BuiltImageReference(string applicationName, string inputReference) =>
        $"relaxkonos-ad/{applicationName}:b{inputReference[..12].ToLowerInvariant()}";

    public static bool IsValidImageReference(string? value) => value is { Length: >= 1 and <= 255 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or ':' or '.' or '_' or '-');

    /// <summary>Rejects any image reference that resolves to a floating tag rather than a version line.</summary>
    public static bool IsPinnedImageReference(string? value)
        => value is not null && !value.EndsWith(":latest", StringComparison.Ordinal) && !value.Contains(":latest@", StringComparison.Ordinal);

    public static string[] Labels(Guid applicationId, string applicationName, Guid? revisionId, int? revisionNumber, Guid? operationId, string role)
    {
        var labels = new List<string>
        {
            $"{ManagedLabel}=true",
            $"{OwnerLabel}={OwnerValue}",
            $"{ApplicationIdLabel}={applicationId:D}",
            $"{ApplicationLabel}={applicationName}",
            $"{RoleLabel}={role}",
        };
        if (revisionId is { } revision) labels.Add($"{RevisionIdLabel}={revision:D}");
        if (revisionNumber is { } number) labels.Add($"{RevisionLabel}={number}");
        if (operationId is { } operation) labels.Add($"{OperationLabel}={operation:D}");
        return [.. labels];
    }

    /// <summary>Reads a single managed label from an inspect result. Only our own prefix is consulted.</summary>
    public static string? Label(IReadOnlyDictionary<string, string>? labels, string name) =>
        labels is not null && labels.TryGetValue(name, out var value) ? value : null;

    public static bool IsManaged(IReadOnlyDictionary<string, string>? labels) =>
        Label(labels, ManagedLabel) == "true" && Label(labels, OwnerLabel) == OwnerValue;
}
