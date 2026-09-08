using System.Collections.Concurrent;

namespace RelaxKonOS.Server.Hubs;

/// <summary>Keeps polling limited to workloads with at least one live log viewer.</summary>
public sealed class GuardianLogSubscriptionRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _connections = new(StringComparer.Ordinal);

    public void Subscribe(string connectionId, string workloadId) =>
        _connections.GetOrAdd(workloadId, _ => new ConcurrentDictionary<string, byte>())[connectionId] = 0;

    public void Unsubscribe(string connectionId, string workloadId)
    {
        if (!_connections.TryGetValue(workloadId, out var subscribers)) return;
        subscribers.TryRemove(connectionId, out _);
        if (subscribers.IsEmpty) _connections.TryRemove(new KeyValuePair<string, ConcurrentDictionary<string, byte>>(workloadId, subscribers));
    }

    public void RemoveConnection(string connectionId)
    {
        foreach (var (workloadId, subscribers) in _connections)
        {
            subscribers.TryRemove(connectionId, out _);
            if (subscribers.IsEmpty) _connections.TryRemove(new KeyValuePair<string, ConcurrentDictionary<string, byte>>(workloadId, subscribers));
        }
    }

    public IReadOnlyList<string> WorkloadIds => _connections
        .Where(pair => !pair.Value.IsEmpty)
        .Select(pair => pair.Key)
        .ToArray();
}
