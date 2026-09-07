using System.Collections.Concurrent;
using RemoteOS.Protocol.Registry;
using Server.Domain;

namespace Server.Storage;

public interface IRegistryRepository
{
    IReadOnlyList<RegistryEntry> List(Guid userId, RegistryScope? scope = null);
    RegistryEntry? Find(Guid userId, RegistryScope scope, Guid scopeId, string path, string name);
    RegistryEntry Upsert(RegistryEntry entry);
    RegistryEntry? CompareExchange(RegistryEntry entry, long expectedRevision);
    bool Delete(Guid userId, RegistryScope scope, Guid scopeId, string path, string name);
    void SeedSynced(RegistryEntry entry);
    IReadOnlyList<RegistryKey> ListChildKeys(Guid userId, RegistryScope scope, Guid scopeId, string parentPath);
    RegistryKey CreateKey(RegistryKey key);
    bool DeleteKeyTree(Guid userId, RegistryScope scope, Guid scopeId, string path);
}

/// <summary>Development fallback with the same tenant key as SQLite.</summary>
public sealed class InMemoryRegistryRepository : IRegistryRepository
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<(Guid, RegistryScope, Guid, string, string), RegistryEntry> _entries = new();
    private readonly ConcurrentDictionary<(Guid, RegistryScope, Guid, string), RegistryKey> _keys = new();
    public IReadOnlyList<RegistryEntry> List(Guid userId, RegistryScope? scope = null) => _entries.Values
        .Where(x => x.UserId == userId && (scope is null || x.Scope == scope))
        .OrderBy(x => x.Scope).ThenBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal).Select(Copy).ToArray();
    public RegistryEntry? Find(Guid userId, RegistryScope scope, Guid scopeId, string path, string name) =>
        _entries.TryGetValue((userId, scope, scopeId, path, name), out var entry) ? Copy(entry) : null;
    public RegistryEntry Upsert(RegistryEntry entry)
    {
        lock (_gate)
        {
        var saved = _entries.AddOrUpdate(Key(entry), _ => Copy(entry), (_, current) =>
        {
            entry.Revision = current.Revision + 1;
            return Copy(entry);
        });
        return Copy(saved);
        }
    }
    public RegistryEntry? CompareExchange(RegistryEntry entry, long expectedRevision)
    {
        lock (_gate)
        {
            var revision = _entries.TryGetValue(Key(entry), out var current) ? current.Revision : 0;
            if (revision != expectedRevision) return null;
            return Upsert(entry);
        }
    }
    public bool Delete(Guid userId, RegistryScope scope, Guid scopeId, string path, string name) => _entries.TryRemove((userId, scope, scopeId, path, name), out _);
    public void SeedSynced(RegistryEntry entry) => _entries.TryAdd(Key(entry), Copy(entry));
    public IReadOnlyList<RegistryKey> ListChildKeys(Guid userId, RegistryScope scope, Guid scopeId, string parentPath) =>
        _keys.Values.Concat(_entries.Values.Select(x => new RegistryKey { UserId = x.UserId, Scope = x.Scope, ScopeId = x.ScopeId, Path = x.Path }))
            .Where(x => x.UserId == userId && x.Scope == scope && x.ScopeId == scopeId && IsDirectChild(x.Path, parentPath))
            .GroupBy(x => x.Path, StringComparer.Ordinal).Select(x => Copy(x.First())).OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();
    public RegistryKey CreateKey(RegistryKey key) => Copy(_keys.GetOrAdd(Key(key), _ => Copy(key)));
    public bool DeleteKeyTree(Guid userId, RegistryScope scope, Guid scopeId, string path)
    {
        var prefix = path + "\\";
        var removed = false;
        foreach (var key in _keys.Keys.Where(x => x.Item1 == userId && x.Item2 == scope && x.Item3 == scopeId && (x.Item4 == path || x.Item4.StartsWith(prefix, StringComparison.Ordinal))).ToArray())
            removed |= _keys.TryRemove(key, out _);
        foreach (var key in _entries.Keys.Where(x => x.Item1 == userId && x.Item2 == scope && x.Item3 == scopeId && (x.Item4 == path || x.Item4.StartsWith(prefix, StringComparison.Ordinal))).ToArray())
            removed |= _entries.TryRemove(key, out _);
        return removed;
    }
    private static (Guid, RegistryScope, Guid, string, string) Key(RegistryEntry x) => (x.UserId, x.Scope, x.ScopeId, x.Path, x.Name);
    private static (Guid, RegistryScope, Guid, string) Key(RegistryKey x) => (x.UserId, x.Scope, x.ScopeId, x.Path);
    private static bool IsDirectChild(string candidate, string parent) => candidate.StartsWith(parent + "\\", StringComparison.Ordinal)
        && candidate[(parent.Length + 1)..].IndexOf('\\') < 0;
    private static RegistryEntry Copy(RegistryEntry x) => new() { UserId = x.UserId, Scope = x.Scope, ScopeId = x.ScopeId, Path = x.Path, Name = x.Name, ValueType = x.ValueType, ValueJson = x.ValueJson, Revision = x.Revision, State = x.State, DesiredUpdatedAt = x.DesiredUpdatedAt, DesiredUpdatedBy = x.DesiredUpdatedBy, AppliedRevision = x.AppliedRevision, AppliedAt = x.AppliedAt, LastErrorCode = x.LastErrorCode, LastErrorMessage = x.LastErrorMessage };
    private static RegistryKey Copy(RegistryKey x) => new() { UserId = x.UserId, Scope = x.Scope, ScopeId = x.ScopeId, Path = x.Path, CreatedAt = x.CreatedAt, CreatedBy = x.CreatedBy };
}
