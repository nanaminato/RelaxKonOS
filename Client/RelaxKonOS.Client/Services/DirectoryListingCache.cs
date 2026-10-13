namespace RelaxKonOS.Client.Services;

/// <summary>Session-owned bounded directory snapshots. Never persisted or used to authorize IO.</summary>
internal sealed class DirectoryListingCache<T>(Func<DateTimeOffset>? clock = null)
{
    private sealed record Snapshot(T Value, int Weight, DateTimeOffset Written, long Access);
    private readonly Dictionary<string, Snapshot> _entries = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private long _access;
    internal const int MaximumDirectories = 64;
    internal const int MaximumEntries = 50000;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public bool TryGet(string path, out T value)
    {
        if (_entries.TryGetValue(path, out var entry) && _clock() - entry.Written < Lifetime)
        {
            _entries[path] = entry with { Access = ++_access };
            value = entry.Value; return true;
        }
        _entries.Remove(path); value = default!; return false;
    }
    public void Put(string path, T value, int weight)
    {
        if (weight > MaximumEntries) { _entries.Remove(path); return; }
        _entries[path] = new(value, weight, _clock(), ++_access);
        while (_entries.Count > MaximumDirectories || _entries.Values.Sum(item => item.Weight) > MaximumEntries)
            _entries.Remove(_entries.MinBy(item => item.Value.Access).Key);
    }
    public void Clear() => _entries.Clear();
}
