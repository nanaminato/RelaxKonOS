using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Files;

/// <summary>A refusal with a stable problem code and the HTTP status it maps to.</summary>
public sealed class UploadSessionException(string problemCode, int statusCode, string detail) : Exception(detail)
{
    public string ProblemCode { get; } = problemCode;

    public int StatusCode { get; } = statusCode;

    /// <summary>Offset the server actually holds, for the two answers that mean "resynchronise and
    /// continue" rather than "give up". Carried so a client does not need a second round trip.</summary>
    public long? AuthoritativeOffset { get; init; }
}

/// <summary>
/// The resumable upload path: open a session, append raw chunks at explicit offsets, commit once.
/// </summary>
/// <remarks>
/// Two properties are load-bearing and must not be traded away:
/// <list type="bullet">
/// <item>The server never buffers a whole file. A chunk is copied straight from the request body into the
/// staging file with a fixed buffer, so memory use is independent of file size.</item>
/// <item>An offset is confirmed only after its bytes were flushed. The value returned to the client is
/// therefore the only value it may treat as received — that is what makes <c>GET</c> authoritative and
/// resumption exact.</item>
/// </list>
/// </remarks>
public sealed class UploadSessionService(
    IFileService files,
    IPrivilegedFileService privileged,
    IFileElevationSessionStore elevations,
    UploadSessionStore store,
    UploadSessionOptions options,
    ILogger<UploadSessionService> logger)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim creationGate = new(1, 1);

    /// <summary>Opens a session, or returns the one the same idempotency key already opened.</summary>
    public async Task<UploadSessionDto> CreateAsync(ClaimsPrincipal user, CreateUploadRequest request,
        string idempotencyKey, CancellationToken cancellationToken)
    {
        var identityKey = IdentityKey(user);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
            throw new UploadSessionException(FileUploadProblemCodes.IdempotencyRequired, 400,
                "创建上传会话必须携带 1–128 字符的 Idempotency-Key。");

        if (!FileUploadNamePolicy.IsValidFileName(request.FileName))
            throw new UploadSessionException(FileUploadProblemCodes.InvalidFileName, 400,
                $"文件名必须是单一成分: {FileUploadNamePolicy.DescribeForLog(request.FileName)}");
        if (string.IsNullOrWhiteSpace(request.TargetDirectoryPath) || !Path.IsPathFullyQualified(request.TargetDirectoryPath))
            throw new UploadSessionException("invalid-path", 400, "目标目录必须是绝对路径。");
        if (request.Length < 0)
            throw new UploadSessionException("invalid-input", 400, "文件长度不能为负。");
        if (request.Length > options.MaximumFileLengthBytes)
            throw new UploadSessionException("invalid-input", 400,
                $"文件长度超出服务端上限 {options.MaximumFileLengthBytes} 字节。");

        // The key check, quota check, and insertion are one operation. Two concurrent retries of the
        // same POST must not open two staging files before either request reaches the index.
        await creationGate.WaitAsync(cancellationToken);
        try { return await CreateUnderGateAsync(user, request, idempotencyKey, identityKey, cancellationToken); }
        finally { creationGate.Release(); }
    }

    private async Task<UploadSessionDto> CreateUnderGateAsync(ClaimsPrincipal user, CreateUploadRequest request,
        string idempotencyKey, string identityKey, CancellationToken cancellationToken)
    {
        var digest = DigestOf(request);
        if (store.FindByIdempotencyKey(identityKey, idempotencyKey) is { } existing)
        {
            if (existing.IsExpired(options, DateTimeOffset.UtcNow))
            {
                // A deterministic client key may be used again after the session lifetime. Expired
                // records must not make a fresh attempt resume a session that GET would reject with 410.
                await AbandonAsync(existing, cancellationToken);
            }
            else
            {
                if (!string.Equals(existing.IdempotencyDigest, digest, StringComparison.Ordinal))
                    throw new UploadSessionException(FileUploadProblemCodes.IdempotencyConflict, 409,
                        "Idempotency-Key 已用于内容不同的请求。");
                // The retry gets the same session back, so a lost response never leaks a staging file and a
                // client can simply continue from the offset reported here.
                return ToDto(existing);
            }
        }

        if (!Directory.Exists(request.TargetDirectoryPath))
            throw new UploadSessionException("not-found", 404, $"目标目录不存在: {request.TargetDirectoryPath}");

        if (store.CountFor(identityKey) >= options.MaximumSessionsPerIdentity || store.Count >= options.MaximumSessions)
            throw new UploadSessionException(FileUploadProblemCodes.TooManyUploads, 429,
                "未完成的上传会话过多，请等待现有上传结束或放弃其中一些。");

        EnsureStagingVolumeHasRoom(request.TargetDirectoryPath, request.Length);

        var sessionId = Guid.NewGuid().ToString("N");
        var stagingPath = Path.Combine(request.TargetDirectoryPath,
            FileUploadNamePolicy.BuildStagingFileName(request.FileName, sessionId));

        var elevated = false;
        try
        {
            files.CreateStagingFile(stagingPath);
        }
        catch (UnauthorizedAccessException)
        {
            if (!elevations.IsElevated(user, FileElevationCapability.Upload, request.TargetDirectoryPath))
                throw new UploadSessionException(FileUploadProblemCodes.ElevationRequired, 403,
                    "目标目录需要管理员授权才能写入。");
            try
            {
                await privileged.CreateStagingAsync(stagingPath, cancellationToken);
                elevated = true;
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UploadSessionException("access-denied", 403, ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                throw new UploadSessionException("privileged-helper-unavailable", 503, ex.Message);
            }
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new UploadSessionException("not-found", 404, ex.Message);
        }

        var now = DateTimeOffset.UtcNow;
        // The chunk size is a property of the session, not of the request: an elevated session must fit its
        // base64 form into one Helper message, so the server, not the client, decides the ceiling.
        var chunkSize = elevated ? FileUploadProtocol.ElevatedChunkSize : FileUploadProtocol.DefaultChunkSize;
        var record = new UploadSessionRecord(sessionId, identityKey, request.TargetDirectoryPath, request.FileName,
            stagingPath, request.Length, 0, chunkSize, elevated, idempotencyKey, digest, now, now);
        try { store.Add(record); }
        catch
        {
            if (elevated)
            {
                try { await privileged.DeleteAsync(stagingPath, CancellationToken.None); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    logger.LogWarning(exception, "Failed to clean up an unindexed staging file. SessionId={SessionId}", sessionId);
                }
            }
            else files.DeleteStagingFile(stagingPath);
            throw;
        }
        logger.LogInformation(
            "File upload session opened. SessionId={SessionId}, Length={Length}, ChunkSize={ChunkSize}, Elevated={Elevated}, TargetDirectoryHash={TargetDirectoryHash}",
            sessionId, request.Length, chunkSize, elevated, Hash(request.TargetDirectoryPath));
        return ToDto(record);
    }

    /// <summary>Reads the authoritative offset. This is how a client removes ambiguity after any doubt.</summary>
    public UploadSessionDto Get(ClaimsPrincipal user, string sessionId)
        => ToDto(RequireLive(user, sessionId));

    /// <summary>Appends one chunk and returns the offset the server now holds.</summary>
    public async Task<long> AppendAsync(ClaimsPrincipal user, string sessionId, long? offset, long? contentLength,
        Stream body, CancellationToken cancellationToken)
    {
        var session = RequireLive(user, sessionId);
        if (contentLength is null)
            throw new UploadSessionException(FileUploadProblemCodes.LengthRequired, 411,
                "分片必须声明 Content-Length：偏移算术要求正文长度已知。");
        if (contentLength.Value > session.ChunkSize)
            throw new UploadSessionException(FileUploadProblemCodes.ChunkTooLarge, 413,
                $"单个分片不得超过 {session.ChunkSize} 字节。")
            { AuthoritativeOffset = session.Offset };
        if (offset is null || offset.Value != session.Offset)
            throw new UploadSessionException(FileUploadProblemCodes.OffsetMismatch, 409,
                $"Upload-Offset 与服务端不一致，服务端当前为 {session.Offset}。")
            { AuthoritativeOffset = session.Offset };
        if (offset.Value + contentLength.Value > session.Length)
            throw new UploadSessionException(FileUploadProblemCodes.LengthExceeded, 409,
                "本次分片会超过会话声明的长度，数据已丢弃。")
            { AuthoritativeOffset = session.Offset };

        // A session-level latch, not just an offset comparison: two chunks racing on the same offset would
        // both pass the check above and then interleave their writes into one file.
        var gate = gates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.Zero, cancellationToken))
            throw new UploadSessionException(FileUploadProblemCodes.ConcurrentChunk, 409,
                "同一会话已有分片正在写入。")
            { AuthoritativeOffset = session.Offset };

        try
        {
            // The first offset check ran before the gate. A previous chunk may have completed while this
            // request was waiting to enter, so never truncate a now-confirmed tail using that stale view.
            var current = RequireLive(user, sessionId);
            if (offset.Value != current.Offset)
                throw new UploadSessionException(FileUploadProblemCodes.OffsetMismatch, 409,
                    $"Upload-Offset 与服务端不一致，服务端当前为 {current.Offset}。")
                { AuthoritativeOffset = current.Offset };
            long newOffset;
            if (session.Elevated)
            {
                var appended = await privileged.AppendChunkAsync(session.StagingPath, offset.Value, body, cancellationToken);
                if (appended != offset.Value + contentLength.Value)
                {
                    // An inconsistent staging file cannot be repaired through the shape-constrained Helper
                    // primitives, so the honest answer is that this session no longer exists. A well-formed
                    // HTTP request cannot produce this state; it is a guard, not a path.
                    await AbandonAsync(session, cancellationToken);
                    throw new UploadSessionException(FileUploadProblemCodes.SessionNotFound, 404,
                        "暂存文件与服务端记录不一致，会话已作废，请重新开始。");
                }
                newOffset = appended;
            }
            else
            {
                try
                {
                    newOffset = await files.AppendStagingAsync(session.StagingPath, offset.Value, contentLength.Value, body, cancellationToken);
                }
                catch (FileNotFoundException)
                {
                    await AbandonAsync(session, cancellationToken);
                    throw new UploadSessionException(FileUploadProblemCodes.SessionNotFound, 404,
                        "暂存文件已不存在，会话已作废，请重新开始。");
                }
                catch (DirectoryNotFoundException)
                {
                    await AbandonAsync(session, cancellationToken);
                    throw new UploadSessionException(FileUploadProblemCodes.SessionNotFound, 404,
                        "目标目录已不存在，会话已作废，请重新开始。");
                }
            }

            store.Advance(sessionId, newOffset, DateTimeOffset.UtcNow);
            return newOffset;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Publishes a fully received session as the destination file. This is the only step that
    /// creates or replaces that file, so a cancelled or failed upload never leaves a partial one behind.</summary>
    public async Task<FileEntryDto> CommitAsync(ClaimsPrincipal user, string sessionId, string? contentHash,
        CancellationToken cancellationToken)
    {
        var session = RequireLive(user, sessionId);
        if (session.Offset != session.Length)
            throw new UploadSessionException(FileUploadProblemCodes.Incomplete, 409,
                $"会话仅收到 {session.Offset}/{session.Length} 字节。")
            { AuthoritativeOffset = session.Offset };

        if (!string.IsNullOrWhiteSpace(contentHash))
            await VerifyHashAsync(session, contentHash, cancellationToken);

        FileEntryDto dto;
        if (session.Elevated)
        {
            dto = await CommitPrivilegedAsync(session, cancellationToken);
        }
        else
        {
            var destination = Path.Combine(session.TargetDirectoryPath, session.FileName);
            try
            {
                dto = files.CommitStagingFile(session.StagingPath, destination);
            }
            catch (UnauthorizedAccessException)
            {
                if (!elevations.IsElevated(user, FileElevationCapability.Upload, session.TargetDirectoryPath))
                    throw new UploadSessionException(FileUploadProblemCodes.ElevationRequired, 403,
                        "目标目录需要管理员授权才能写入。");
                dto = await CommitPrivilegedAsync(session, cancellationToken);
            }
            catch (DirectoryNotFoundException ex)
            {
                throw new UploadSessionException("not-found", 404, ex.Message);
            }
        }

        store.Remove(sessionId);
        gates.TryRemove(sessionId, out _);
        logger.LogInformation(
            "File upload session committed. SessionId={SessionId}, Bytes={Bytes}, Elevated={Elevated}, TargetDirectoryHash={TargetDirectoryHash}",
            sessionId, session.Length, session.Elevated, Hash(session.TargetDirectoryPath));
        return dto;
    }

    /// <summary>Abandons a session: the staging file is removed (best effort) and the record dropped.
    /// Idempotent — abandoning an unknown session is a success, because the caller's goal is "not there".</summary>
    public async Task AbortAsync(ClaimsPrincipal user, string sessionId, CancellationToken cancellationToken)
    {
        if (!store.TryGet(sessionId, out var session) || !OwnedBy(session, user)) return;
        await AbandonAsync(session, cancellationToken);
        logger.LogInformation("File upload session abandoned. SessionId={SessionId}, Bytes={Bytes}", sessionId, session.Offset);
    }

    /// <summary>Removes sessions past their lifetime. Runs from the index only.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var removed = 0;
        foreach (var session in store.Snapshot())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!session.IsExpired(options, now)) continue;
            await AbandonAsync(session, cancellationToken);
            removed++;
            logger.LogInformation("File upload session expired and was removed. SessionId={SessionId}, Bytes={Bytes}, CreatedAt={CreatedAt}",
                session.SessionId, session.Offset, session.CreatedAt);
        }
        return removed;
    }

    private async Task<FileEntryDto> CommitPrivilegedAsync(UploadSessionRecord session, CancellationToken cancellationToken)
    {
        try
        {
            return await privileged.CommitAsync(session.StagingPath, session.FileName, cancellationToken);
        }
        catch (FileNotFoundException ex) { throw new UploadSessionException("not-found", 404, ex.Message); }
        catch (UnauthorizedAccessException ex) { throw new UploadSessionException("access-denied", 403, ex.Message); }
        catch (InvalidOperationException ex) { throw new UploadSessionException("privileged-helper-unavailable", 503, ex.Message); }
    }

    /// <summary>Drops the session and its staging file. Delegates deletion of a protected file to the Helper.</summary>
    private async Task AbandonAsync(UploadSessionRecord session, CancellationToken cancellationToken)
    {
        store.Remove(session.SessionId);
        gates.TryRemove(session.SessionId, out _);
        if (session.Elevated)
        {
            try { await privileged.DeleteAsync(session.StagingPath, cancellationToken); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // The host may already have removed it, or the Helper may be down. Nothing to report: the
                // record is gone, so the client is told the session does not exist.
            }
        }
        else
        {
            files.DeleteStagingFile(session.StagingPath);
        }
    }

    private async Task VerifyHashAsync(UploadSessionRecord session, string contentHash, CancellationToken cancellationToken)
    {
        var expected = contentHash.StartsWith("sha256-", StringComparison.OrdinalIgnoreCase)
            ? contentHash["sha256-".Length..] : contentHash;
        string actual;
        await using (var stream = new FileStream(session.StagingPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        }
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new UploadSessionException(FileUploadProblemCodes.HashMismatch, 409,
                "声明的内容哈希与实收字节不符，目标文件未被替换。");
    }

    private UploadSessionRecord RequireLive(ClaimsPrincipal user, string sessionId)
    {
        if (!store.TryGet(sessionId, out var session) || !OwnedBy(session, user))
            // Not found and not yours answer the same thing, so a session id cannot be probed for existence.
            throw new UploadSessionException(FileUploadProblemCodes.SessionNotFound, 404, "上传会话不存在或不属于当前身份。");
        if (session.IsExpired(options, DateTimeOffset.UtcNow))
            throw new UploadSessionException(FileUploadProblemCodes.SessionExpired, 410, "上传会话已过期，请重新开始。");
        return session;
    }

    private static bool OwnedBy(UploadSessionRecord session, ClaimsPrincipal user)
        => string.Equals(session.IdentityKey, IdentityKey(user), StringComparison.Ordinal);

    /// <summary>
    /// The identity a session is bound to. The server knows the authenticated subject; sessions are
    /// deliberately not portable between identities (nor between a user token and a file-capability token
    /// of a different subject), because resuming someone else's session would be resuming into their file.
    /// </summary>
    private static string IdentityKey(ClaimsPrincipal user)
        => user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new UnauthorizedAccessException();

    private static string DigestOf(CreateUploadRequest request) => Hash(JsonSerializer.Serialize(request, RelaxKonOSJsonOptions.Default));

    /// <summary>Hashes a value for a log line. Paths and names are not written verbatim.</summary>
    private static string Hash(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    private void EnsureStagingVolumeHasRoom(string targetDirectoryPath, long length)
    {
        var available = AvailableFreeSpace(targetDirectoryPath);
        if (available is null)
        {
            // Not measurable on every host. Refusing every upload because a number is missing would be
            // worse than the risk the guard exists to avoid.
            logger.LogDebug("Available space could not be read for {Hash}; the staging volume guard is skipped.", Hash(targetDirectoryPath));
            return;
        }
        if (available.Value < length + options.StagingVolumeReserveBytes)
            throw new UploadSessionException(FileUploadProblemCodes.InsufficientStorage, 507,
                "暂存卷可用空间不足（含保留量），已拒绝创建上传会话。");
    }

    private static long? AvailableFreeSpace(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root)) return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private UploadSessionDto ToDto(UploadSessionRecord session)
        => new(session.SessionId, session.Offset, session.Length, session.ChunkSize, session.Elevated,
            session.ExpiresAt(options));
}
