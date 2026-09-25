using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Files;

/// <summary>
/// Fixed numbers of the resumable upload protocol. They live in the shared protocol so the server
/// enforces and both clients request the same values; a client never invents a limit of its own.
/// </summary>
public static class FileUploadProtocol
{
    /// <summary>Chunk size the server advertises for an ordinary session (raw bytes over HTTP).</summary>
    public const int DefaultChunkSize = 8 * 1024 * 1024;

    /// <summary>Chunk size for an elevated session: base64 widens it by 4/3, so 6 MiB stays well under
    /// <see cref="Privileged.PrivilegedOperationProtocol.MaximumFileContentBytes"/>.</summary>
    public const int ElevatedChunkSize = 6 * 1024 * 1024;

    /// <summary>Hard ceiling of the single-shot route. Declared explicitly instead of inheriting a
    /// framework default, so exceeding it is a named answer rather than an opaque 413.</summary>
    public const long SingleShotMaximumBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Client-side dispatch threshold: at or below this a file still travels in one request. It is a
    /// scheduling choice, not a contract — the server never depends on it.
    /// </summary>
    public const long SingleShotThresholdBytes = 4 * 1024 * 1024;

    /// <summary>Largest declared file length a session may open, unless the host configures otherwise.</summary>
    public const long DefaultMaximumFileLength = 1024L * 1024 * 1024 * 1024;

    /// <summary>Suffix of the staging file that lives beside its destination while it is being received.</summary>
    public const string StagingExtension = ".rkup";

    /// <summary>Hex length of a session identifier as it appears inside a staging file name.</summary>
    public const int SessionIdHexLength = 32;

    /// <summary>Carries the authoritative byte offset: on a chunk request (required) and on a
    /// conflict answer (advisory, so a client can resynchronise without an extra round trip).</summary>
    public const string OffsetHeader = "Upload-Offset";

    /// <summary>Makes session creation retry-safe: the same key and the same body return the same session.</summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";
}

/// <summary>
/// Stable problem codes of the resumable upload surface. Clients branch on these; a raw I/O message is
/// never a product message. The value doubles as the RFC 7807 <c>type</c> suffix and as the
/// <c>problemCode</c> extension.
/// </summary>
public static class FileUploadProblemCodes
{
    /// <summary>The supplied name is not a single file-name component (separators, dots, reserved name, too long).</summary>
    public const string InvalidFileName = "invalid-file-name";

    /// <summary>The staging volume cannot hold the declared length plus the configured reserve.</summary>
    public const string InsufficientStorage = "insufficient-storage";

    /// <summary>Too many open sessions for this identity, or globally.</summary>
    public const string TooManyUploads = "too-many-uploads";

    /// <summary>An idempotency key was reused with a different request body.</summary>
    public const string IdempotencyConflict = "idempotency-conflict";

    /// <summary>Session creation arrived without an idempotency key. Creating a session is the one
    /// step that allocates a staging file, so it must be safe to repeat after a lost response.</summary>
    public const string IdempotencyRequired = "idempotency-required";

    /// <summary>Unknown session, or one owned by another identity. Both answer this, so absence is not disclosed.</summary>
    public const string SessionNotFound = "upload-session-not-found";

    /// <summary>The session outlived its idle or absolute lifetime.</summary>
    public const string SessionExpired = "upload-session-expired";

    /// <summary>The chunk offset disagrees with the confirmed offset. Not a failure: resynchronise and continue.</summary>
    public const string OffsetMismatch = "upload-offset-mismatch";

    /// <summary>A second chunk is already being written for this session.</summary>
    public const string ConcurrentChunk = "upload-concurrent-chunk";

    /// <summary>This chunk would push the session past its declared length.</summary>
    public const string LengthExceeded = "upload-length-exceeded";

    /// <summary>The chunk is larger than the session chunk size.</summary>
    public const string ChunkTooLarge = "upload-chunk-too-large";

    /// <summary>A chunk arrived without a Content-Length, so its end cannot be located.</summary>
    public const string LengthRequired = "length-required";

    /// <summary>Commit was requested before the declared length had been received.</summary>
    public const string Incomplete = "upload-incomplete";

    /// <summary>The declared hash does not match the received bytes.</summary>
    public const string HashMismatch = "upload-hash-mismatch";

    /// <summary>The single-shot route was handed more than it declares it accepts.</summary>
    public const string TooLargeForSingleShot = "upload-too-large-for-single-shot";

    /// <summary>The session could not create its staging file and no elevation grant covers the directory.</summary>
    public const string ElevationRequired = "elevation-required";
}

/// <summary>Opens an upload session for one file. The declared length is what every offset is measured against.</summary>
public sealed record CreateUploadRequest(
    [property: JsonPropertyName("targetDirectoryPath")] string TargetDirectoryPath,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("length")] long Length,
    [property: JsonPropertyName("lastModifiedUtc")] DateTimeOffset? LastModifiedUtc = null);

/// <summary>
/// State of an upload session. <see cref="Offset"/> is the single source of truth for resumption:
/// a client that is unsure what the server received asks for it instead of inferring an answer.
/// </summary>
public sealed record UploadSessionDto(
    [property: JsonPropertyName("uploadId")] string UploadId,
    [property: JsonPropertyName("offset")] long Offset,
    [property: JsonPropertyName("length")] long Length,
    [property: JsonPropertyName("chunkSize")] int ChunkSize,
    [property: JsonPropertyName("elevated")] bool Elevated,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

/// <summary>Turns a fully received session into the destination file. The hash is optional; when given it must match.</summary>
public sealed record CommitUploadRequest(
    [property: JsonPropertyName("contentHash")] string? ContentHash = null);
