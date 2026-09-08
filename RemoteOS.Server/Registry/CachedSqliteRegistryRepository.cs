using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using RemoteOS.Protocol.Registry;
using Server.Domain;
using Server.Storage;
using Server.Storage.Sqlite;

namespace Server.ConfigurationRegistry;

/// <summary>
/// The configuration registry's authoritative runtime copy. Reads and mutations are served
/// from memory; SQLite is a durable, delayed write-behind copy used for restart recovery.
/// </summary>
public sealed class CachedSqliteRegistryRepository(IDbContextFactory<RemoteOsDbContext> dbFactory) : IRegistryRepository, IHostedService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<EntryKey, RegistryEntry> _entries = new();
    private readonly ConcurrentDictionary<KeyKey, RegistryKey> _keys = new();
    private readonly HashSet<EntryKey> _dirty = [];
    private readonly HashSet<EntryKey> _deleted = [];
    private readonly HashSet<KeyKey> _dirtyKeys = [];
    private readonly HashSet<KeyKey> _deletedKeys = [];
    private readonly CancellationTokenSource _stopping = new();
    private Task? _flushLoop;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entries = await db.RegistryEntries.AsNoTracking().ToListAsync(cancellationToken);
        foreach (var entry in entries)
        {
            // Workspace JSON columns were retired in favour of these direct registry keys.
            // Keep their old registry projections out of the runtime source without mutating
            // a user's historical database rows.
            if (!IsRetiredWorkspaceConfiguration(entry.Path))
                _entries[EntryKey.From(entry)] = Copy(entry);
        }
        var keys = await db.RegistryKeys.AsNoTracking().ToListAsync(cancellationToken);
        foreach (var key in keys)
            _keys[KeyKey.From(key)] = Copy(key);

        _flushLoop = FlushLoopAsync(_stopping.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        if (_flushLoop is not null)
        {
            try { await _flushLoop; }
            catch (OperationCanceledException) { }
        }
        await FlushAsync(cancellationToken);
        _stopping.Dispose();
    }

    public IReadOnlyList<RegistryEntry> List(Guid userId, RegistryScope? scope = null) => _entries.Values
        .Where(x => x.UserId == userId && (scope is null || x.Scope == scope))
        .OrderBy(x => x.Scope).ThenBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal)
        .Select(Copy).ToArray();

    public RegistryEntry? Find(Guid userId, RegistryScope scope, Guid scopeId, string path, string name) =>
        _entries.TryGetValue(new EntryKey(userId, scope, scopeId, path, name), out var entry) ? Copy(entry) : null;

    public RegistryEntry Upsert(RegistryEntry entry)
    {
        var key = EntryKey.From(entry);
        lock (_gate)
        {
            var saved = Copy(entry);
            saved.Revision = _entries.TryGetValue(key, out var current) ? current.Revision + 1 : Math.Max(1, saved.Revision);
            // PendingSync makes the delayed-persistence state explicit to API consumers.
            saved.State = RegistryEntryState.PendingSync;
            saved.AppliedRevision = null;
            saved.AppliedAt = null;
            saved.LastErrorCode = null;
            saved.LastErrorMessage = null;
            _entries[key] = saved;
            _deleted.Remove(key);
            _dirty.Add(key);
            return Copy(saved);
        }
    }

    public RegistryEntry? CompareExchange(RegistryEntry entry, long expectedRevision)
    {
        lock (_gate)
        {
            var revision = _entries.TryGetValue(EntryKey.From(entry), out var current) ? current.Revision : 0;
            if (revision != expectedRevision) return null;
            return Upsert(entry);
        }
    }

    public bool Delete(Guid userId, RegistryScope scope, Guid scopeId, string path, string name)
    {
        var key = new EntryKey(userId, scope, scopeId, path, name);
        lock (_gate)
        {
            if (!_entries.TryRemove(key, out _)) return false;
            _dirty.Remove(key);
            _deleted.Add(key);
            return true;
        }
    }

    public void SeedSynced(RegistryEntry entry)
    {
        var key = EntryKey.From(entry);
        _entries.TryAdd(key, Copy(entry));
    }

    public IReadOnlyList<RegistryKey> ListChildKeys(Guid userId, RegistryScope scope, Guid scopeId, string parentPath) =>
        _keys.Values.Concat(_entries.Values.Select(x => new RegistryKey { UserId = x.UserId, Scope = x.Scope, ScopeId = x.ScopeId, Path = x.Path }))
            .Where(x => x.UserId == userId && x.Scope == scope && x.ScopeId == scopeId && IsDirectChild(x.Path, parentPath))
            .GroupBy(x => x.Path, StringComparer.Ordinal).Select(x => Copy(x.First())).OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();

    public RegistryKey CreateKey(RegistryKey key)
    {
        var keyId = KeyKey.From(key);
        lock (_gate)
        {
            if (_keys.TryGetValue(keyId, out var existing)) return Copy(existing);
            var saved = Copy(key);
            _keys[keyId] = saved;
            _deletedKeys.Remove(keyId);
            _dirtyKeys.Add(keyId);
            return Copy(saved);
        }
    }

    public bool DeleteKeyTree(Guid userId, RegistryScope scope, Guid scopeId, string path)
    {
        var prefix = path + "\\";
        lock (_gate)
        {
            var entryKeys = _entries.Keys.Where(x => x.UserId == userId && x.Scope == scope && x.ScopeId == scopeId
                && (x.Path == path || x.Path.StartsWith(prefix, StringComparison.Ordinal))).ToArray();
            var keyKeys = _keys.Keys.Where(x => x.UserId == userId && x.Scope == scope && x.ScopeId == scopeId
                && (x.Path == path || x.Path.StartsWith(prefix, StringComparison.Ordinal))).ToArray();
            if (entryKeys.Length == 0 && keyKeys.Length == 0) return false;
            foreach (var key in entryKeys)
            {
                _entries.TryRemove(key, out _);
                _dirty.Remove(key);
                _deleted.Add(key);
            }
            foreach (var key in keyKeys)
            {
                _keys.TryRemove(key, out _);
                _dirtyKeys.Remove(key);
                _deletedKeys.Add(key);
            }
            return true;
        }
    }

    private async Task FlushLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        Dictionary<EntryKey, RegistryEntry> dirty;
        EntryKey[] deleted;
        Dictionary<KeyKey, RegistryKey> dirtyKeys;
        KeyKey[] deletedKeys;
        lock (_gate)
        {
            if (_dirty.Count == 0 && _deleted.Count == 0 && _dirtyKeys.Count == 0 && _deletedKeys.Count == 0) return;
            dirty = _dirty
                .Where(key => _entries.TryGetValue(key, out _))
                .ToDictionary(key => key, key => Copy(_entries[key]));
            deleted = _deleted.ToArray();
            dirtyKeys = _dirtyKeys
                .Where(key => _keys.TryGetValue(key, out _))
                .ToDictionary(key => key, key => Copy(_keys[key]));
            deletedKeys = _deletedKeys.ToArray();
        }

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            foreach (var entry in dirty.Values)
            {
                var existing = await db.RegistryEntries.FindAsync(
                    [entry.UserId, entry.Scope, entry.ScopeId, entry.Path, entry.Name], cancellationToken);
                if (existing is null)
                {
                    var durable = Copy(entry);
                    durable.State = RegistryEntryState.Synced;
                    durable.AppliedRevision = durable.Revision;
                    durable.AppliedAt = DateTimeOffset.UtcNow;
                    db.RegistryEntries.Add(durable);
                }
                else
                    CopyInto(entry, existing);
            }
            foreach (var key in deleted)
            {
                var existing = await db.RegistryEntries.FindAsync(
                    [key.UserId, key.Scope, key.ScopeId, key.Path, key.Name], cancellationToken);
                if (existing is not null) db.RegistryEntries.Remove(existing);
            }
            foreach (var key in dirtyKeys.Values)
            {
                var existing = await db.RegistryKeys.FindAsync([key.UserId, key.Scope, key.ScopeId, key.Path], cancellationToken);
                if (existing is null) db.RegistryKeys.Add(Copy(key));
            }
            foreach (var key in deletedKeys)
            {
                var existing = await db.RegistryKeys.FindAsync([key.UserId, key.Scope, key.ScopeId, key.Path], cancellationToken);
                if (existing is not null) db.RegistryKeys.Remove(existing);
            }
            await db.SaveChangesAsync(cancellationToken);

            lock (_gate)
            {
                foreach (var (key, flushed) in dirty)
                {
                    if (_entries.TryGetValue(key, out var current) && current.Revision == flushed.Revision)
                    {
                        current.State = RegistryEntryState.Synced;
                        current.AppliedRevision = current.Revision;
                        current.AppliedAt = DateTimeOffset.UtcNow;
                        _dirty.Remove(key);
                    }
                }
                foreach (var key in deleted)
                {
                    // A later upsert wins over the delete snapshot and remains dirty.
                    if (!_entries.ContainsKey(key)) _deleted.Remove(key);
                }
                foreach (var key in dirtyKeys.Keys)
                {
                    if (_keys.ContainsKey(key)) _dirtyKeys.Remove(key);
                }
                foreach (var key in deletedKeys)
                {
                    if (!_keys.ContainsKey(key)) _deletedKeys.Remove(key);
                }
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Keep the dirty snapshot in memory for the next scheduled retry. The runtime
            // registry remains usable even during a transient database outage.
        }
    }

    private static RegistryEntry Copy(RegistryEntry x) => new()
    {
        UserId = x.UserId, Scope = x.Scope, ScopeId = x.ScopeId, Path = x.Path, Name = x.Name,
        ValueType = x.ValueType, ValueJson = x.ValueJson, Revision = x.Revision, State = x.State,
        DesiredUpdatedAt = x.DesiredUpdatedAt, DesiredUpdatedBy = x.DesiredUpdatedBy,
        AppliedRevision = x.AppliedRevision, AppliedAt = x.AppliedAt,
        LastErrorCode = x.LastErrorCode, LastErrorMessage = x.LastErrorMessage,
    };

    private static void CopyInto(RegistryEntry source, RegistryEntry target)
    {
        target.ValueType = source.ValueType;
        target.ValueJson = source.ValueJson;
        target.Revision = source.Revision;
        target.State = RegistryEntryState.Synced;
        target.DesiredUpdatedAt = source.DesiredUpdatedAt;
        target.DesiredUpdatedBy = source.DesiredUpdatedBy;
        target.AppliedRevision = source.Revision;
        target.AppliedAt = DateTimeOffset.UtcNow;
        target.LastErrorCode = null;
        target.LastErrorMessage = null;
    }

    private static bool IsDirectChild(string candidate, string parent) => candidate.StartsWith(parent + "\\", StringComparison.Ordinal)
        && candidate[(parent.Length + 1)..].IndexOf('\\') < 0;

    private static bool IsRetiredWorkspaceConfiguration(string path) => path is
        "Workspace\\Terminal\\Appearance" or "Workspace\\Desktop\\Preferences" or "Workspace\\Browser\\Settings";

    private static RegistryKey Copy(RegistryKey x) => new()
    {
        UserId = x.UserId, Scope = x.Scope, ScopeId = x.ScopeId, Path = x.Path,
        CreatedAt = x.CreatedAt, CreatedBy = x.CreatedBy,
    };

    private readonly record struct EntryKey(Guid UserId, RegistryScope Scope, Guid ScopeId, string Path, string Name)
    {
        public static EntryKey From(RegistryEntry entry) => new(entry.UserId, entry.Scope, entry.ScopeId, entry.Path, entry.Name);
    }
    private readonly record struct KeyKey(Guid UserId, RegistryScope Scope, Guid ScopeId, string Path)
    {
        public static KeyKey From(RegistryKey key) => new(key.UserId, key.Scope, key.ScopeId, key.Path);
    }
}
