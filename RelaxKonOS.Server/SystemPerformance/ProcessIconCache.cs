namespace RelaxKonOS.Server.SystemPerformance;

/// <summary>Instance-owned FIFO cache. Loading runs outside the lock; only retained payload is budgeted.</summary>
internal sealed class ProcessIconCache : IDisposable
{
    internal const int MaximumEntries = 512;
    internal const int MaximumBytes = 8 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();
    private int _bytes;
    private bool _disposed;

    internal (int Entries, int Bytes, int OrderEntries) Describe()
    { lock (_gate) return (_entries.Count, _bytes, _order.Count); }

    internal ProcessIconContent GetOrAdd(string key, Func<ProcessIconContent> load)
    {
        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(key, out entry!))
            {
                while (_entries.Count >= MaximumEntries) RemoveOldest();
                entry = new(new Lazy<ProcessIconContent>(load, LazyThreadSafetyMode.ExecutionAndPublication), _order.AddLast(key));
                _entries.Add(key, entry);
            }
        }
        ProcessIconContent content;
        try { content = entry.Content.Value; }
        catch
        {
            lock (_gate) if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry)) Remove(key, entry);
            throw;
        }
        lock (_gate)
        {
            // Evicted or disposed work may finish for its caller, but cannot repopulate the cache.
            if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry) && !entry.Accounted)
            {
                if (content.Data.Length > MaximumBytes) Remove(key, entry);
                else
                {
                    entry.Bytes = content.Data.Length; entry.Accounted = true; _bytes += entry.Bytes;
                    while (_bytes > MaximumBytes) RemoveOldest();
                }
            }
        }
        return content;
    }

    private void RemoveOldest()
    {
        var key = _order.First!.Value;
        Remove(key, _entries[key]);
    }

    private void Remove(string key, Entry entry)
    { _entries.Remove(key); _order.Remove(entry.Node); _bytes -= entry.Bytes; }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _entries.Clear(); _order.Clear(); _bytes = 0; }
    }

    private sealed class Entry(Lazy<ProcessIconContent> content, LinkedListNode<string> node)
    {
        internal Lazy<ProcessIconContent> Content { get; } = content;
        internal LinkedListNode<string> Node { get; } = node;
        internal bool Accounted;
        internal int Bytes;
    }
}
