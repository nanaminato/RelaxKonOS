using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Uploads;

/// <summary>What one file needs to be uploaded: where it comes from, where it goes, and how big it is.</summary>
public sealed record LargeFileUploadRequest(
    string SourcePath,
    string TargetDirectoryPath,
    string FileName,
    long Length,
    DateTimeOffset LastWriteUtc);

/// <summary>
/// Progress of one large upload, split the way the number is actually known: bytes the server has
/// confirmed, and bytes of the current chunk already handed to the socket. A caller that shows
/// <c>confirmed + inFlight</c> never displays more than the server can already have, which is what stops
/// the bar from filling up while the data is still local.
/// </summary>
/// <param name="Reconciling">True while the client is re-reading the offset after a doubt. Callers should
/// say "checking with the server" rather than moving the bar backwards.</param>
public sealed record LargeFileUploadProgress(long ConfirmedBytes, long InFlightBytes, bool Reconciling);

/// <summary>Uploads one file through the resumable session protocol.</summary>
public interface ILargeFileUploader
{
    Task<FileEntryDto> UploadAsync(LargeFileUploadRequest request,
        Func<IReadOnlyList<string>, FileElevationCapability, Task<bool>> requestElevation,
        Action<LargeFileUploadProgress>? progress, CancellationToken ct = default);
}

/// <summary>
/// Drives one session from "nothing sent" to "file exists on the server".
/// </summary>
/// <remarks>
/// The rules that make resumption exact, and that this type exists to enforce:
/// <list type="bullet">
/// <item>Any doubt about what the server received is resolved by asking it (<see cref="IExplorerUploadChannel.GetSessionAsync"/>),
/// never by assuming an in-flight chunk arrived.</item>
/// <item>An offset reported by the server is adopted even when it is lower than the local view. The local
/// view is an estimate; the server's number is the truth.</item>
/// <item>Exhausting the retry budget does not delete the session or the journal entry, so "retry" continues
/// from the offset rather than starting again.</item>
/// </list>
/// </remarks>
/// <param name="retryDelay">Backoff between chunk retries. Injectable so a check can exhaust the retry budget
/// without waiting out real delays; the default is exponential with jitter.</param>
public sealed class LargeFileUploader(
    IExplorerUploadChannel channel,
    UploadResumeJournal journal,
    Func<string?> serverKey,
    Func<int, TimeSpan>? retryDelay = null) : ILargeFileUploader
{
    /// <summary>Chunk attempts allowed before the file is reported as failed (the session is kept).</summary>
    public const int MaximumTransportFailures = 10;

    /// <summary>
    /// Chunk size floor after repeated failures. Smaller chunks retry more cheaply on a bad link.
    /// It is a preference, not a right: see <see cref="FloorOf"/>, which clamps it to what the server allows.
    /// </summary>
    private const int MinimumChunkSize = 1024 * 1024;

    /// <summary>How long a chunk may make no progress before it is cancelled and retried.</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    public async Task<FileEntryDto> UploadAsync(LargeFileUploadRequest request,
        Func<IReadOnlyList<string>, FileElevationCapability, Task<bool>> requestElevation,
        Action<LargeFileUploadProgress>? progress, CancellationToken ct = default)
    {
        if (!File.Exists(request.SourcePath))
            throw new FileNotFoundException($"本地文件不存在: {request.SourcePath}", request.SourcePath);
        if (!FileUploadNamePolicyBridge.IsSingleComponent(request.FileName))
            throw new ArgumentException($"文件名必须是单一成分: {request.FileName}", nameof(request));

        var key = serverKey();
        UploadSessionDto session;
        try
        {
            session = await EnsureSessionAsync(request, key, requestElevation, progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled during the one round trip that asks about a remembered session. Cancelling is
            // terminal, so that session and its entry go together. A session this attempt had itself just
            // opened is the one case nothing can be done about: its id never reached the client, so the
            // server's idle lifetime is the only cleanup left.
            if (key is not null
                && journal.Find(key, request.SourcePath, request.Length, request.LastWriteUtc) is { } remembered)
                await AbandonAsync(remembered.UploadId);
            throw;
        }

        var uploadId = session.UploadId;
        var confirmed = session.Offset;
        var chunkSize = CeilingOf(session);
        var floor = FloorOf(session);
        var failures = 0;

        using var source = new FileStream(request.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            // A file that changed after the session was opened would be published as a mix of two versions.
            if (source.Length != request.Length)
            {
                await AbandonAsync(uploadId);
                throw new IOException("本地文件在上传期间发生变化，已放弃该会话。");
            }

            Record(key, request, uploadId);
            await PumpAsync();

            while (true)
            {
                try
                {
                    var entry = await channel.CommitAsync(uploadId, null, ct);
                    journal.Remove(uploadId);
                    return entry;
                }
                catch (UploadChannelException exception) when (exception.ProblemCode == FileUploadProblemCodes.Incomplete
                                                              || exception.ProblemCode == FileUploadProblemCodes.OffsetMismatch)
                {
                    // The server holds fewer bytes than the client believed. Adopt its number and send the
                    // remainder instead of failing a transfer that is nearly done.
                    var authoritative = exception.AuthoritativeOffset
                        ?? (await channel.GetSessionAsync(uploadId, ct)).Offset;
                    confirmed = authoritative;
                    if (authoritative >= request.Length)
                        throw new IOException("服务端长度与本地不一致，已保留会话；请重试以重新核对。");
                    await PumpAsync();
                }
                catch (UploadChannelException exception) when (exception.IsElevationRequired)
                {
                    // The directory refused a write that succeeded at create time. Authorization is asked for
                    // once more here; a session that was created directly normally never reaches this branch.
                    if (!await requestElevation([request.TargetDirectoryPath], FileElevationCapability.Upload))
                        throw new UnauthorizedAccessException("目标目录需要管理员授权。");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelling is terminal and must leave nothing behind. Without this, a cancelled multi-gigabyte
            // upload keeps a hidden staging file in the user's own directory for the rest of the session's
            // idle lifetime, and the journal still offers to "continue" a file the user cancelled.
            await AbandonAsync(uploadId);
            throw;
        }

        async Task PumpAsync()
        {
            while (confirmed < request.Length)
            {
                ct.ThrowIfCancellationRequested();
                var length = Math.Min(chunkSize, request.Length - confirmed);
                try
                {
                    var newOffset = await SendChunkAsync(uploadId, source, confirmed, length, progress, ct);
                    if (newOffset <= confirmed)
                        throw new UploadChannelException(null, null, null, isTransport: true, "服务端未确认任何新字节。");
                    confirmed = newOffset;
                    failures = 0;
                    var ceiling = CeilingOf(session);
                    if (chunkSize < ceiling) chunkSize = Math.Min(ceiling, chunkSize * 2);
                    progress?.Invoke(new(confirmed, 0, false));
                }
                catch (UploadChannelException exception) when (exception.IsSessionLost)
                {
                    // The server does not know this session any more: open a fresh one rather than retrying.
                    journal.Remove(uploadId);
                    session = await CreateSessionAsync(request, key, requestElevation, ct);
                    uploadId = session.UploadId;
                    confirmed = session.Offset;
                    chunkSize = CeilingOf(session);
                    floor = FloorOf(session);
                    Record(key, request, uploadId);
                }
                catch (UploadChannelException exception) when (exception.IsUnauthorized)
                {
                    if (!await channel.RefreshSessionAsync(ct))
                        throw new UnauthorizedAccessException("登录状态已失效，请重新登录后重试。");
                    confirmed = await ResynchroniseAsync(uploadId, exception.AuthoritativeOffset, progress, ct);
                }
                catch (UploadChannelException exception) when (exception.CanContinue)
                {
                    // "Resynchronise and continue" is a normal answer, not a failure — but every one of these
                    // answers is "not a failure", so a server that keeps giving the same one would spin this
                    // loop forever. An answer that leaves the offset where it was is counted against the same
                    // budget as a transport failure; an answer that moved it made progress and is not counted.
                    // A chunk that was too large is the one refusal with an obvious remedy, so it is shrunk
                    // rather than merely retried.
                    var previous = confirmed;
                    if (exception.ProblemCode == FileUploadProblemCodes.ChunkTooLarge)
                        chunkSize = Math.Max(floor, chunkSize / 2);
                    confirmed = await ResynchroniseAsync(uploadId, exception.AuthoritativeOffset, progress, ct);
                    if (confirmed <= previous)
                    {
                        if (++failures >= MaximumTransportFailures)
                            throw new IOException(
                                $"上传失败（服务端连续 {failures} 次未确认新字节），会话已保留，可稍后继续。", exception);
                        await DelayAsync(failures, ct);
                    }
                }
                catch (Exception exception) when (exception is UploadChannelException or HttpRequestException
                                                      or IOException or OperationCanceledException)
                {
                    if (ct.IsCancellationRequested) throw;
                    if (++failures >= MaximumTransportFailures)
                        // Keep the session and the journal entry: the user's retry continues from here.
                        throw new IOException($"上传失败（已重试 {failures} 次），会话已保留，可稍后继续。", exception);
                    await DelayAsync(failures, ct);
                    chunkSize = Math.Max(floor, chunkSize / 2);
                    confirmed = await ResynchroniseAsync(uploadId, null, progress, ct);
                }
            }
        }
    }

    private async Task<UploadSessionDto> EnsureSessionAsync(LargeFileUploadRequest request, string? key,
        Func<IReadOnlyList<string>, FileElevationCapability, Task<bool>> requestElevation,
        Action<LargeFileUploadProgress>? progress, CancellationToken ct)
    {
        if (key is not null
            && journal.Find(key, request.SourcePath, request.Length, request.LastWriteUtc) is { } remembered)
        {
            try
            {
                var existing = await channel.GetSessionAsync(remembered.UploadId, ct);
                progress?.Invoke(new(existing.Offset, 0, false));
                return existing;
            }
            catch (UploadChannelException exception) when (exception.IsSessionLost || exception.StatusCode == 403)
            {
                journal.Remove(remembered.UploadId);
            }
            catch (Exception exception) when (exception is UploadChannelException or HttpRequestException
                                                  or IOException or OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                // Cannot confirm the remembered session right now (offline, server restarting). Opening a
                // second session for the same file would leak a staging file and split the transfer, so the
                // failure is reported and the entry is kept for the next attempt.
                throw new IOException("无法确认上次的上传进度，请检查网络后重试。", exception);
            }
        }

        return await CreateSessionAsync(request, key, requestElevation, ct);
    }

    private async Task<UploadSessionDto> CreateSessionAsync(LargeFileUploadRequest request, string? key,
        Func<IReadOnlyList<string>, FileElevationCapability, Task<bool>> requestElevation, CancellationToken ct)
    {
        var body = new CreateUploadRequest(request.TargetDirectoryPath, request.FileName, request.Length, request.LastWriteUtc);
        try
        {
            return await channel.CreateSessionAsync(body, Guid.NewGuid().ToString("N"), ct);
        }
        catch (UploadChannelException exception) when (exception.IsElevationRequired)
        {
            // Elevation is settled once, here. The session then carries the decision, so no chunk can be
            // refused mid-transfer and no dialog appears after forty minutes of uploading.
            if (!await requestElevation([request.TargetDirectoryPath], FileElevationCapability.Upload))
                throw new UnauthorizedAccessException("目标目录需要管理员授权。");
            return await channel.CreateSessionAsync(body, Guid.NewGuid().ToString("N"), ct);
        }
    }

    private async Task<long> SendChunkAsync(string uploadId, Stream source, long offset, long length,
        Action<LargeFileUploadProgress>? progress, CancellationToken ct)
    {
        using var stall = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stall.Token);
        using var watchdog = new StallWatchdog(StallTimeout, stall);
        long inFlight = 0;
        watchdog.Touch();
        try
        {
            return await channel.SendChunkAsync(uploadId, offset, length, source, () =>
            {
                inFlight += 81_920;
                if (inFlight > length) inFlight = length;
                watchdog.Touch();
                progress?.Invoke(new(offset, inFlight, false));
            }, linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The watchdog fired: the connection is half-dead and would otherwise hang forever, because the
            // upload client deliberately has no whole-request timeout.
            throw new UploadChannelException(null, null, null, isTransport: true, "分片长时间无进展，已中止并重试。");
        }
    }

    private async Task<long> ResynchroniseAsync(string uploadId, long? hint, Action<LargeFileUploadProgress>? progress,
        CancellationToken ct)
    {
        progress?.Invoke(new(0, 0, true));
        if (hint is { } offset) return offset;
        try { return (await channel.GetSessionAsync(uploadId, ct)).Offset; }
        catch (UploadChannelException exception) when (exception.IsSessionLost)
        {
            throw new UploadChannelException(exception.ProblemCode, exception.StatusCode, null, false, exception.Message);
        }
    }

    private void Record(string? key, LargeFileUploadRequest request, string uploadId)
    {
        if (key is null) return;
        journal.Record(new UploadJournalEntry(uploadId, key, request.TargetDirectoryPath, request.FileName,
            request.SourcePath, request.Length, request.LastWriteUtc, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Leaves nothing behind after a cancellation: the entry goes first, so a later attempt cannot be offered
    /// to continue a file the user cancelled, then the session. Both steps are best effort — cancelling is a
    /// decision the user already made, and it must not be reported as a failure because a cleanup step could
    /// not be completed. A session the client never learns the id of is left to the server's idle lifetime,
    /// which exists for exactly this.
    /// </summary>
    private async Task AbandonAsync(string uploadId)
    {
        try { journal.Remove(uploadId); }
        catch (Exception) { /* A journal that cannot be written is a separate problem; the cancel stands. */ }
        try { await channel.AbortAsync(uploadId, CancellationToken.None); }
        catch (Exception) { /* The server expires it on its own. */ }
    }

    private Task DelayAsync(int failures, CancellationToken ct)
        => Task.Delay(retryDelay?.Invoke(failures) ?? DefaultRetryDelay(failures), ct);

    /// <summary>
    /// The largest chunk this session accepts. The server sets the route's body limit from the value it
    /// advertises here, so sending more is refused every single time rather than occasionally
    /// (`RelaxKonOS.FileUpload.Design.md` §3.8).
    /// </summary>
    private static int CeilingOf(UploadSessionDto session) => Math.Max(1, session.ChunkSize);

    /// <summary>
    /// The smallest chunk a failing transfer shrinks to, never above the server's ceiling. Pinning the
    /// floor at the client's preferred 1 MiB would leave a session that allows less in a loop of
    /// "refused every time, shrunk never" until the retry budget ran out.
    /// </summary>
    private static int FloorOf(UploadSessionDto session) => Math.Min(MinimumChunkSize, CeilingOf(session));

    /// <summary>Exponential backoff with jitter, so many clients on one server do not retry in lockstep.</summary>
    private static TimeSpan DefaultRetryDelay(int failures)
    {
        var seconds = Math.Min(30, Math.Pow(2, Math.Min(failures, 5)));
        var jitter = Random.Shared.NextDouble() * 0.25 + 0.875;
        return TimeSpan.FromSeconds(seconds * jitter);
    }

    /// <summary>
    /// Cancels its token when no byte has moved for the configured interval. A client with no whole-request
    /// timeout needs this: without it, a half-open connection would wait forever rather than retrying.
    /// </summary>
    private sealed class StallWatchdog : IDisposable
    {
        private readonly TimeSpan _timeout;
        private readonly CancellationTokenSource _target;
        private readonly object _gate = new();
        private readonly Task _loop;
        private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;

        public StallWatchdog(TimeSpan timeout, CancellationTokenSource target)
        {
            _timeout = timeout;
            _target = target;
            _loop = MonitorAsync();
        }

        public void Touch()
        {
            lock (_gate) _lastActivity = DateTimeOffset.UtcNow;
        }

        private async Task MonitorAsync()
        {
            while (!_target.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch (Exception) { return; }
                bool stalled;
                lock (_gate) stalled = DateTimeOffset.UtcNow - _lastActivity > _timeout;
                if (!stalled) continue;
                try { _target.Cancel(); } catch (ObjectDisposedException) { }
                return;
            }
        }

        public void Dispose()
        {
            try { _target.Cancel(); } catch (ObjectDisposedException) { }
            _ = _loop;
        }
    }
}

/// <summary>
/// The name rule a client applies before it starts: the server enforces the same thing and answers
/// <c>invalid-file-name</c>, but failing early gives the user a readable message without a round trip.
/// Kept as a small mirror of the server policy rather than shared code, because the server policy is
/// about host paths and lives with them.
/// </summary>
internal static class FileUploadNamePolicyBridge
{
    public static bool IsSingleComponent(string? name)
        => !string.IsNullOrWhiteSpace(name)
            && name.Length <= 255
            && name is not ("." or "..")
            && name[^1] is not ('.' or ' ' or ':')
            && name.IndexOfAny(['/', '\\', '\0', ':', '*', '?', '"', '<', '>', '|']) < 0
            && !name.Any(char.IsControl);
}
