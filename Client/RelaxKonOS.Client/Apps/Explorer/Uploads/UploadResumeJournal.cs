using System.Text.Json;

namespace RelaxKonOS.Client.Apps.Explorer.Uploads;

/// <summary>
/// One unfinished upload, remembered so a restart, a crash, or a lost response continues from the
/// server's offset instead of re-sending a file that may already be half transferred.
/// </summary>
/// <param name="UploadId">Server-assigned session identifier.</param>
/// <param name="ServerKey">Server / user / workspace / device the session belongs to. A journal entry is
/// never used against a different one: resuming into another server's session is resuming into someone
/// else's file.</param>
/// <param name="SourceLength">Length of the local file when the session was opened.</param>
/// <param name="SourceLastWriteUtc">Last write time of the local file when the session was opened.</param>
public sealed record UploadJournalEntry(
    string UploadId,
    string ServerKey,
    string TargetDirectoryPath,
    string FileName,
    string SourcePath,
    long SourceLength,
    DateTimeOffset SourceLastWriteUtc,
    DateTimeOffset RecordedAt);

/// <summary>
/// Device-local record of unfinished uploads. It holds no file content and no credential, only the
/// identifiers needed to ask the server where to continue.
/// </summary>
public sealed class UploadResumeJournal
{
    /// <summary>Matches the server's absolute session lifetime; an older entry cannot be resumed anyway.</summary>
    public static readonly TimeSpan MaximumAge = TimeSpan.FromDays(7);

    /// <summary>Oldest entries are dropped beyond this count, so the file cannot grow without bound.</summary>
    public const int MaximumEntries = 64;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<UploadJournalEntry> _entries = [];
    private bool _loaded;

    public UploadResumeJournal(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RelaxKonOS", "upload-resume.json");
    }

    /// <summary>Path of the journal file, so a test can point it somewhere disposable.</summary>
    public string JournalPath => _path;

    /// <summary>
    /// Finds an entry that can be resumed for this exact source file on this exact server. A local file
    /// whose length or timestamp changed is not the file the session started with, so resuming would
    /// produce a file that is neither the old nor the new one; such an entry is discarded instead.
    /// </summary>
    public UploadJournalEntry? Find(string serverKey, string sourcePath, long length, DateTimeOffset lastWriteUtc)
    {
        lock (_gate)
        {
            EnsureLoaded();
            for (var index = _entries.Count - 1; index >= 0; index--)
            {
                var entry = _entries[index];
                if (!string.Equals(entry.ServerKey, serverKey, StringComparison.Ordinal)
                    || !string.Equals(entry.SourcePath, sourcePath, StringComparison.Ordinal)) continue;
                if (entry.SourceLength != length || entry.SourceLastWriteUtc != lastWriteUtc)
                {
                    _entries.RemoveAt(index);
                    Save();
                    continue;
                }
                return entry;
            }
            return null;
        }
    }

    public void Record(UploadJournalEntry entry)
    {
        lock (_gate)
        {
            EnsureLoaded();
            // One entry per source-and-destination. The upload id alone would not be enough: a session the
            // server had forgotten is replaced by a new one with a new id, and leaving its predecessor behind
            // means a later resume can pick a session that no longer exists.
            _entries.RemoveAll(existing =>
                string.Equals(existing.UploadId, entry.UploadId, StringComparison.Ordinal)
                || (string.Equals(existing.ServerKey, entry.ServerKey, StringComparison.Ordinal)
                    && string.Equals(existing.SourcePath, entry.SourcePath, StringComparison.Ordinal)
                    && string.Equals(existing.TargetDirectoryPath, entry.TargetDirectoryPath, StringComparison.Ordinal)
                    && string.Equals(existing.FileName, entry.FileName, StringComparison.Ordinal)));
            _entries.Add(entry);
            Trim();
            Save();
        }
    }

    public void Remove(string uploadId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_entries.RemoveAll(entry => string.Equals(entry.UploadId, uploadId, StringComparison.Ordinal)) > 0) Save();
        }
    }

    /// <summary>Drops every entry that belongs to a server key no longer in use. Called when the signed-in
    /// server/user/workspace/device changes, so a stale entry can never be offered to a new session.</summary>
    public void RemoveServer(string serverKey)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_entries.RemoveAll(entry => !string.Equals(entry.ServerKey, serverKey, StringComparison.Ordinal)) > 0) Save();
        }
    }

    public IReadOnlyList<UploadJournalEntry> Snapshot()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return [.. _entries];
        }
    }

    private void Trim()
    {
        var cutoff = DateTimeOffset.UtcNow - MaximumAge;
        _entries.RemoveAll(entry => entry.RecordedAt < cutoff);
        if (_entries.Count <= MaximumEntries) return;
        _entries.Sort((left, right) => left.RecordedAt.CompareTo(right.RecordedAt));
        _entries.RemoveRange(0, _entries.Count - MaximumEntries);
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<List<UploadJournalEntry>>(File.ReadAllText(_path));
            if (loaded is null) return;
            _entries.AddRange(loaded.Where(entry => !string.IsNullOrWhiteSpace(entry.UploadId)));
            Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable journal means "no resumable uploads", which costs at most a retransmission.
            // Failing the upload because a bookkeeping file is corrupt would be far worse.
            _entries.Clear();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Journal writes are best effort for the same reason reads are: never fail the transfer over one.
        }
    }
}
