using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Uploads;

/// <summary>
/// A refusal from the upload data plane. Unlike an ordinary API failure this one carries the two facts
/// the orchestrator needs to decide what to do next: whether the server gave a verdict at all, and the
/// offset the server actually holds.
/// </summary>
public sealed class UploadChannelException(
    string? problemCode,
    int? statusCode,
    long? authoritativeOffset,
    bool isTransport,
    string detail) : Exception(detail)
{
    /// <summary>Stable server code, when the server named one.</summary>
    public string? ProblemCode { get; } = problemCode;

    public int? StatusCode { get; } = statusCode;

    /// <summary>The offset the server holds, from the response header. Absent means "ask with GET".</summary>
    public long? AuthoritativeOffset { get; } = authoritativeOffset;

    /// <summary>True when the request produced no server verdict: a transport failure or an unnamed 5xx.</summary>
    public bool IsTransport { get; } = isTransport;

    /// <summary>True when the session is gone or expired, so the only correct action is a fresh start.</summary>
    public bool IsSessionLost => ProblemCode is FileUploadProblemCodes.SessionNotFound or FileUploadProblemCodes.SessionExpired;

    /// <summary>True when the token was refused, so the next attempt must use a refreshed one.</summary>
    public bool IsUnauthorized => StatusCode == 401;

    /// <summary>True when the destination needs administrator authorization.</summary>
    public bool IsElevationRequired => ProblemCode == FileUploadProblemCodes.ElevationRequired;

    /// <summary>
    /// True when the answer means "resynchronise and keep going" rather than "give up": the server is
    /// telling the client where it actually is. The offset-mismatch answer has the same standing as
    /// <c>thumbnail-unsupported</c> does in image preview — a normal reply, not a failure.
    /// </summary>
    public bool CanContinue => ProblemCode is FileUploadProblemCodes.OffsetMismatch
        or FileUploadProblemCodes.ConcurrentChunk
        or FileUploadProblemCodes.ChunkTooLarge
        or FileUploadProblemCodes.LengthRequired;
}

/// <summary>
/// The upload data plane. It is a separate abstraction from <c>IExplorerClient</c> on purpose: the
/// ordinary client's request pipeline buffers a request body so it can replay it after a 401, which is
/// unusable for a multi-gigabyte body. Nothing here ever holds a whole chunk in managed memory on
/// behalf of the transport.
/// </summary>
public interface IExplorerUploadChannel
{
    /// <summary>Opens a session. <paramref name="idempotencyKey"/> makes a retried call return the same one.</summary>
    Task<UploadSessionDto> CreateSessionAsync(CreateUploadRequest request, string idempotencyKey, CancellationToken ct = default);

    /// <summary>Reads the authoritative offset. The only way to resolve any doubt about what arrived.</summary>
    Task<UploadSessionDto> GetSessionAsync(string uploadId, CancellationToken ct = default);

    /// <summary>
    /// Sends byte range <c>[offset, offset + length)</c> of <paramref name="source"/> as the next chunk.
    /// Returns the offset the server confirmed. <paramref name="onInFlight"/> reports bytes handed to the
    /// socket, which the caller turns into a progress estimate and a stall signal.
    /// </summary>
    Task<long> SendChunkAsync(string uploadId, long offset, long length, Stream source, Action? onInFlight,
        CancellationToken ct = default);

    /// <summary>Publishes the session as the destination file.</summary>
    Task<FileEntryDto> CommitAsync(string uploadId, string? contentHash, CancellationToken ct = default);

    /// <summary>Abandons the session. Best effort: a failure is left to the server's expiry sweep.</summary>
    Task AbortAsync(string uploadId, CancellationToken ct = default);

    /// <summary>Refreshes the access token after a 401. Returns false when the session is really gone.</summary>
    Task<bool> RefreshSessionAsync(CancellationToken ct = default);
}
