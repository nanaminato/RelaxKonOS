using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Protocol.Files;

[JsonConverter(typeof(JsonStringEnumConverter<FileElevationCapability>))]
public enum FileElevationCapability
{
    Read,
    Write,
    CreateDirectory,
    Delete,
    Rename,
    Move,
    Copy,
    Upload,
}

/// <summary>Verifies administrator access. With server sessionValid enabled, verification is remembered
/// for this access token until expiry; otherwise the grant covers the requested capability/path for five minutes.
/// Scoped read grants for directories cover descendants; scoped read grants for files cover only that file.</summary>
public sealed record FileElevationRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("password")] string? Password = null,
    [property: JsonPropertyName("relatedPaths")] IReadOnlyList<string>? RelatedPaths = null,
    [property: JsonPropertyName("includeDescendants")] bool IncludeDescendants = false,
    [property: JsonPropertyName("capability")] FileElevationCapability? Capability = null,
    [property: JsonPropertyName("administratorUsername")] string? AdministratorUsername = null);

/// <summary>Result of testing or granting elevated access for a single file path.</summary>
public sealed record FileElevationResult(
    [property: JsonPropertyName("requiresElevation")] bool RequiresElevation,
    [property: JsonPropertyName("elevated")] bool Elevated,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt = null);
