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

/// <summary>Prepares capability-scoped access for the current token for five minutes.
/// Read grants for directories also cover descendants; read grants for files cover only that file.</summary>
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
