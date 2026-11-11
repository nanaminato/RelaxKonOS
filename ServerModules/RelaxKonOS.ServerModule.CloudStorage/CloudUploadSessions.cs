using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.ServerModule.CloudStorage;

internal sealed class CloudUploadSessions(string dataPath)
{
    internal const long MaximumFileBytes = 4L * 1024 * 1024 * 1024;
    private const long QuotaBytes = 2 * MaximumFileBytes;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private readonly string _root = Path.Combine(dataPath, "uploads");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<string, Task> _publishing = [];
    private Task? _sweeper;

    internal void StartSweep() => _sweeper = SweepAsync();

    private sealed record Session(string Id, Guid Owner, Guid Connection, string Revision, string Key, string Path,
        long Length, DateTimeOffset? LastModifiedUtc, long Offset, DateTimeOffset ExpiresAt,
        string State = "receiving", int FailureStatus = 0);

    internal async Task<IResult> CreateAsync(Guid owner, Guid connection, string revision, string key, string path,
        long length, DateTimeOffset? lastModifiedUtc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || length < 0)
            return Problem(400, "cloud-upload-invalid");
        if (length > MaximumFileBytes) return Problem(413, "cloud-upload-file-too-large");
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_root);
            var active = new List<Session>();
            foreach (var file in Directory.EnumerateFiles(_root, "*.json"))
            {
                var record = await ReadAsync(Path.GetFileNameWithoutExtension(file), ct);
                if (record is null) continue;
                if (record.ExpiresAt <= DateTimeOffset.UtcNow && !_publishing.ContainsKey(record.Id)) Remove(record);
                else active.Add(record);
            }

            var previous = active.FirstOrDefault(record => record.Owner == owner && record.Connection == connection
                && record.Revision == revision && record.Key == key);
            if (previous is not null)
                return previous.Path == path && previous.Length == length && previous.LastModifiedUtc == lastModifiedUtc
                    ? Results.Ok(Describe(previous))
                    : Problem(409, FileUploadProblemCodes.IdempotencyConflict);
            if (active.Count >= 4096 || active.Count(record => record.State is "receiving" or "publishing") >= 128
                || active.Count(record => record.Owner == owner && record.State is "receiving" or "publishing") >= 8)
                return Problem(429, FileUploadProblemCodes.TooManyUploads);
            var reserved = active.Where(record => File.Exists(Data(record.Id))).Sum(record => record.Length);
            var remaining = active.Where(record => File.Exists(Data(record.Id))).Sum(record => record.Length - record.Offset);
            if (reserved + length > QuotaBytes
                || new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_root))!).AvailableFreeSpace < length + remaining)
                return Problem(507, FileUploadProblemCodes.InsufficientStorage);
            var session = new Session(Guid.NewGuid().ToString("N"), owner, connection, revision, key, path, length, lastModifiedUtc, 0,
                DateTimeOffset.UtcNow + Lifetime);
            using (File.Open(Data(session.Id), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            try { await SaveAsync(session, ct); }
            catch { File.Delete(Data(session.Id)); throw; }
            return Results.Ok(Describe(session));
        }
        finally { _gate.Release(); }
    }

    internal async Task<IResult> HandleAsync(HttpContext http, Guid owner, Guid connection, string revision, string id,
        string action, long? offset, long? length, string? contentHash,
        Func<string, Stream, CancellationToken, Task> publish)
    {
        var ct = http.RequestAborted;
        await _gate.WaitAsync(ct);
        try
        {
            var session = await ReadAsync(id, ct);
            if (session is null || session.Owner != owner || session.Connection != connection || session.Revision != revision)
                return Problem(404, FileUploadProblemCodes.SessionNotFound);
            if (session.ExpiresAt <= DateTimeOffset.UtcNow && !_publishing.ContainsKey(id))
            {
                Remove(session);
                return Problem(410, FileUploadProblemCodes.SessionExpired);
            }
            if (action == "get") return Results.Ok(Describe(session));
            if (action == "abort")
            {
                if (_publishing.ContainsKey(id)) return Problem(409, "cloud-upload-publishing");
                Remove(session);
                return Results.NoContent();
            }
            if (action == "append")
            {
                http.Response.Headers[FileUploadProtocol.OffsetHeader] = session.Offset.ToString(CultureInfo.InvariantCulture);
                if (session.State != "receiving") return Problem(409, "cloud-upload-not-receiving");
                if (length is null or <= 0) return Problem(411, FileUploadProblemCodes.LengthRequired);
                if (length > FileUploadProtocol.DefaultChunkSize) return Problem(413, FileUploadProblemCodes.ChunkTooLarge);
                if (offset != session.Offset) return Problem(409, FileUploadProblemCodes.OffsetMismatch);
                if (length > session.Length - session.Offset) return Problem(409, FileUploadProblemCodes.LengthExceeded);
                long confirmed;
                try
                {
                    using var chunkDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    chunkDeadline.CancelAfter(TimeSpan.FromSeconds(90));
                    confirmed = await ResumableUploadStaging.AppendAsync(Data(id), session.Offset, length.Value,
                        http.Request.Body, chunkDeadline.Token, requireEndOfBody: true);
                }
                catch (Exception error) when (error is EndOfStreamException or InvalidDataException)
                {
                    return Problem(400, "cloud-upload-chunk-length-mismatch");
                }
                await SaveAsync(session with { Offset = confirmed, ExpiresAt = DateTimeOffset.UtcNow + Lifetime }, ct);
                http.Response.Headers[FileUploadProtocol.OffsetHeader] = confirmed.ToString(CultureInfo.InvariantCulture);
                return Results.NoContent();
            }

            if (session.State == "complete") return Results.Ok(Entry(session));
            if (session.State == "failed") return Problem(session.FailureStatus, "cloud-upload-publish-failed");
            if (session.State == "publishing")
                return _publishing.ContainsKey(id) ? Results.Accepted() : Problem(409, "cloud-upload-publish-unconfirmed");
            if (session.Offset != session.Length) return Problem(409, FileUploadProblemCodes.Incomplete);
            if (contentHash is not null && (contentHash.Length != 64 || !contentHash.All(char.IsAsciiHexDigit)))
                return Problem(400, "cloud-upload-invalid-hash");
            if (_publishing.Count >= 4) return Problem(429, FileUploadProblemCodes.TooManyUploads);
            session = session with { State = "publishing", ExpiresAt = DateTimeOffset.UtcNow + Lifetime };
            await SaveAsync(session, ct);
            _publishing.Add(id, PublishAsync(session, contentHash, publish));
            return Results.Accepted();
        }
        finally { _gate.Release(); }
    }

    private async Task PublishAsync(Session session, string? contentHash, Func<string, Stream, CancellationToken, Task> publish)
    {
        var status = 0;
        try
        {
            await using var file = File.OpenRead(Data(session.Id));
            if (contentHash is not null && !Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(file,
                _stopping.Token)).Equals(contentHash, StringComparison.OrdinalIgnoreCase))
                status = 422;
            else
            {
                file.Position = 0;
                await publish(session.Path, file, _stopping.Token);
            }
        }
        catch (Exception error)
        {
            var upstream = error switch
            {
                HttpRequestException http => http.StatusCode,
                Amazon.Runtime.AmazonServiceException aws => aws.StatusCode,
                _ => null,
            };
            status = upstream is System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.Conflict ? 409 : 502;
        }

        await _gate.WaitAsync();
        try
        {
            await SaveAsync(session with { State = status == 0 ? "complete" : "failed", FailureStatus = status }, default);
            File.Delete(Data(session.Id));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { _publishing.Remove(session.Id); _gate.Release(); }
    }

    internal async Task StopAsync(CancellationToken ct)
    {
        await _stopping.CancelAsync();
        if (_sweeper is not null) await _sweeper.WaitAsync(ct);
        await _gate.WaitAsync(ct);
        Task[] jobs;
        try { jobs = _publishing.Values.ToArray(); }
        finally { _gate.Release(); }
        await Task.WhenAll(jobs).WaitAsync(ct);
    }

    private async Task SweepAsync()
    {
        var ct = _stopping.Token;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        try
        {
            do
            {
                await _gate.WaitAsync(ct);
                try
                {
                    if (!Directory.Exists(_root)) continue;
                    foreach (var file in Directory.EnumerateFiles(_root, "*.json"))
                    {
                        var session = await ReadAsync(Path.GetFileNameWithoutExtension(file), ct);
                        if (session is not null && session.ExpiresAt <= DateTimeOffset.UtcNow
                            && !_publishing.ContainsKey(session.Id)) Remove(session);
                    }
                    foreach (var file in Directory.EnumerateFiles(_root, "*" + FileUploadProtocol.StagingExtension))
                        if (!File.Exists(Metadata(Path.GetFileNameWithoutExtension(file)))) File.Delete(file);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
                finally { _gate.Release(); }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task<Session?> ReadAsync(string id, CancellationToken ct)
    {
        if (id.Length != 32 || !id.All(char.IsAsciiHexDigitLower)) return null;
        var file = Metadata(id);
        return File.Exists(file) ? JsonSerializer.Deserialize<Session>(await File.ReadAllBytesAsync(file, ct)) : null;
    }

    private async Task SaveAsync(Session session, CancellationToken ct)
    {
        var file = Metadata(session.Id);
        var temporary = file + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(session), ct);
            File.Move(temporary, file, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private void Remove(Session session)
    {
        File.Delete(Data(session.Id));
        File.Delete(Metadata(session.Id));
    }

    private string Data(string id) => Path.Combine(_root, id + FileUploadProtocol.StagingExtension);
    private string Metadata(string id) => Path.Combine(_root, id + ".json");

    private static UploadSessionDto Describe(Session session) =>
        new(session.Id, session.Offset, session.Length, FileUploadProtocol.DefaultChunkSize, false, session.ExpiresAt);

    private static FileEntryDto Entry(Session session) => new(CloudTransport.Reference(session.Path),
        session.Path.Split('/')[^1], Path.GetExtension(session.Path), session.Length, null, null, null, false, false, "application/octet-stream");

    private static IResult Problem(int status, string code) =>
        Results.Problem(statusCode: status, title: code, extensions: new Dictionary<string, object?> { ["problemCode"] = code });
}
