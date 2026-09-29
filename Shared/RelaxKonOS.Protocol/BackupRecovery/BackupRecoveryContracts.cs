using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.BackupRecovery;

/// <summary>Durable state of a server-side backup or recovery operation.</summary>
public enum BackupRecoveryOperationState
{
    Queued,
    Running,
    Verified,
    Failed,
    Interrupted,
}

/// <summary>Objects that a backup manifest can name. A value is never inferred from a file suffix.</summary>
public enum BackupObjectKind
{
    ApplicationDefinition,
    ApplicationRevision,
    ManagedVolume,
    Database,
}

/// <summary>Secret-free description of one encrypted backup object.</summary>
public sealed record BackupObjectDto(
    [property: JsonPropertyName("kind")] BackupObjectKind Kind,
    [property: JsonPropertyName("reference")] string Reference,
    [property: JsonPropertyName("consistencyMethod")] string ConsistencyMethod,
    [property: JsonPropertyName("length")] long Length,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>
/// An immutable backup manifest projection. The encrypted data key is intentionally absent: only a
/// key identifier and algorithm reach a client or a diagnostic surface.
/// </summary>
public sealed record BackupManifestDto(
    [property: JsonPropertyName("backupId")] Guid BackupId,
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("applicationId")] Guid ApplicationId,
    [property: JsonPropertyName("revisionId")] Guid? RevisionId,
    [property: JsonPropertyName("state")] BackupRecoveryOperationState State,
    [property: JsonPropertyName("keyId")] string KeyId,
    [property: JsonPropertyName("encryptionAlgorithm")] string EncryptionAlgorithm,
    [property: JsonPropertyName("objects")] IReadOnlyList<BackupObjectDto> Objects,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("verifiedAt")] DateTimeOffset? VerifiedAt = null,
    [property: JsonPropertyName("problemCode")] string? ProblemCode = null);

/// <summary>Server capability projection. An unavailable recovery key domain never permits a write.</summary>
public sealed record BackupRecoveryAvailabilityDto(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("keyId")] string? KeyId = null);

/// <summary>Read-only restore admission result. A false value never authorizes a fallback restore.</summary>
public sealed record BackupRestorePreflightDto(
    [property: JsonPropertyName("backupId")] Guid BackupId,
    [property: JsonPropertyName("sourceApplicationId")] Guid SourceApplicationId,
    [property: JsonPropertyName("suggestedApplicationName")] string? SuggestedApplicationName,
    [property: JsonPropertyName("createsNewInstance")] bool CreatesNewInstance,
    [property: JsonPropertyName("canRestore")] bool CanRestore,
    [property: JsonPropertyName("blockers")] IReadOnlyList<string> Blockers);

/// <summary>
/// Explicit request to materialize an admissible definition backup as a new, stopped application.
/// It never authorizes replacing the source application, its volumes or its secret bindings.
/// </summary>
public sealed record RestoreApplicationDefinitionRequest(
    [property: JsonPropertyName("newApplicationName")] string NewApplicationName,
    [property: JsonPropertyName("confirmed")] bool Confirmed = false);
