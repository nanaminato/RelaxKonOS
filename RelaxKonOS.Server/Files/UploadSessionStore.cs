using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Server.Files;

/// <summary>Limits and lifetimes of the resumable upload surface. Every value is enforced server-side.</summary>
public sealed class UploadSessionOptions
{
    /// <summary>ContentRoot-relative directory holding the session index. Kept out of the business
    /// database: it is server-private bookkeeping, like the application-deployment ledger.</summary>
    public string RootDirectory { get; set; } = "data/file-uploads";

    /// <summary>Largest declared file length a session may open.</summary>
    public long MaximumFileLengthBytes { get; set; } = FileUploadProtocol.DefaultMaximumFileLength;

    /// <summary>Free space the staging volume must retain beyond the declared length. A host whose disk
    /// fills up stops serving everything, so a transfer is refused before it can do that.</summary>
    public long StagingVolumeReserveBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>Concurrent unfinished sessions one identity may hold.</summary>
    public int MaximumSessionsPerIdentity { get; set; } = 4;

    /// <summary>Concurrent unfinished sessions across the whole host.</summary>
    public int MaximumSessions { get; set; } = 64;

    /// <summary>How long a session may go without a chunk before it expires.</summary>
    public int IdleLifetimeMinutes { get; set; } = 120;

    /// <summary>Hard ceiling on a session's age, whatever its activity. Bounds the lifetime of the
    /// elevation decision a session carries.</summary>
    public int AbsoluteLifetimeDays { get; set; } = 7;

    /// <summary>How often the sweeper looks for expired or orphaned sessions.</summary>
    public int SweepIntervalMinutes { get; set; } = 15;

    public TimeSpan IdleLifetime => TimeSpan.FromMinutes(Math.Max(1, IdleLifetimeMinutes));

    public TimeSpan AbsoluteLifetime => TimeSpan.FromDays(Math.Max(1, AbsoluteLifetimeDays));

    public TimeSpan SweepInterval => TimeSpan.FromMinutes(Math.Clamp(SweepIntervalMinutes, 1, 1440));
}

/// <summary>
/// One in-flight upload. It is the only record of "what the server already has", so it survives a server
/// restart: without it, a client resuming a multi-hour transfer would be told to start again, which is
/// not support for large files.
/// </summary>
public sealed record UploadSessionRecord(
    string SessionId,
    string IdentityKey,
    string TargetDirectoryPath,
    string FileName,
    string StagingPath,
    long Length,
    long Offset,
    int ChunkSize,
    bool Elevated,
    string? IdempotencyKey,
    string? IdempotencyDigest,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt)
{
    public DateTimeOffset ExpiresAt(UploadSessionOptions options)
    {
        var idle = LastActivityAt + options.IdleLifetime;
        var absolute = CreatedAt + options.AbsoluteLifetime;
        return idle < absolute ? idle : absolute;
    }

    public bool IsExpired(UploadSessionOptions options, DateTimeOffset now) => ExpiresAt(options) <= now;
}

/// <summary>
/// Persistent index of upload sessions. Restart recovery and cleanup both start from this index rather
/// than from a scan of the filesystem: the server may only ever remove files it can attribute to one of
/// its own sessions, and "find every file that looks like a staging file" is neither safe nor cheap.
/// </summary>
public sealed class UploadSessionStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, UploadSessionRecord> sessions = new(StringComparer.Ordinal);
    private readonly string indexPath;
    private readonly UploadSessionOptions options;
    private readonly ILogger<UploadSessionStore> logger;

    public UploadSessionStore(IHostEnvironment environment, UploadSessionOptions options, ILogger<UploadSessionStore> logger)
    {
        this.options = options;
        this.logger = logger;
        var directory = Path.Combine(environment.ContentRootPath, options.RootDirectory);
        Directory.CreateDirectory(directory);
        indexPath = Path.Combine(directory, "index.json");
        Load();
    }

    /// <summary>Path of the index file. Exposed so a test can corrupt it deliberately.</summary>
    public string IndexPath => indexPath;

    public IReadOnlyList<UploadSessionRecord> Snapshot()
    {
        lock (gate) return [.. sessions.Values];
    }

    public bool TryGet(string? sessionId, out UploadSessionRecord record)
    {
        record = null!;
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        lock (gate) return sessions.TryGetValue(sessionId, out record!);
    }

    /// <summary>The identity that already opened a session for this key, if any. Makes creation retry-safe.</summary>
    public UploadSessionRecord? FindByIdempotencyKey(string identityKey, string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return null;
        lock (gate)
        {
            foreach (var session in sessions.Values)
                if (string.Equals(session.IdempotencyKey, idempotencyKey, StringComparison.Ordinal)
                    && string.Equals(session.IdentityKey, identityKey, StringComparison.Ordinal))
                    return session;
        }
        return null;
    }

    public int CountFor(string identityKey)
    {
        lock (gate)
        {
            var count = 0;
            foreach (var session in sessions.Values)
                if (string.Equals(session.IdentityKey, identityKey, StringComparison.Ordinal)) count++;
            return count;
        }
    }

    public int Count
    {
        get { lock (gate) return sessions.Count; }
    }

    public void Add(UploadSessionRecord record)
    {
        lock (gate)
        {
            sessions[record.SessionId] = record;
            try { Persist(); }
            catch { sessions.Remove(record.SessionId); throw; }
        }
    }

    /// <summary>Advances the confirmed offset and activity stamp of a live session.</summary>
    public UploadSessionRecord? Advance(string sessionId, long offset, DateTimeOffset activity)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(sessionId, out var session)) return null;
            var updated = session with { Offset = offset, LastActivityAt = activity };
            sessions[sessionId] = updated;
            try { Persist(); }
            catch { sessions[sessionId] = session; throw; }
            return updated;
        }
    }

    public bool Remove(string sessionId)
    {
        lock (gate)
        {
            if (!sessions.Remove(sessionId, out var session)) return false;
            try { Persist(); }
            catch { sessions[sessionId] = session; throw; }
            return true;
        }
    }

    private void Load()
    {
        if (!File.Exists(indexPath)) return;
        try
        {
            var json = File.ReadAllText(indexPath);
            if (string.IsNullOrWhiteSpace(json)) return;
            var loaded = JsonSerializer.Deserialize<List<UploadSessionRecord>>(json, RelaxKonOSJsonOptions.Default);
            if (loaded is null) return;
            foreach (var session in loaded)
                if (!string.IsNullOrWhiteSpace(session.SessionId))
                    sessions[session.SessionId] = session;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // The index is unreadable. Continue with no sessions at all rather than guessing: a client
            // asking about an unknown session is told exactly that, and the staging files it cannot
            // attribute are left untouched for a later version or an operator to collect.
            sessions.Clear();
            logger.LogWarning(exception, "File upload session index could not be read; continuing with no sessions. Index={IndexPath}", indexPath);
        }
    }

    private void Persist()
    {
        var temporary = indexPath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(sessions.Values.ToArray(), RelaxKonOSJsonOptions.Default));
            File.Move(temporary, indexPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "File upload session index could not be written. Index={IndexPath}", indexPath);
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            throw;
        }
    }
}
