using System;
using System.Collections.Generic;

namespace RelaxKonOS.Server.SystemPerformance;

/// <summary>Tracks distinct PerformanceHub connections so collection only runs while a client needs it.</summary>
public sealed class PerformanceSubscriptionRegistry
{
    private readonly object _gate = new();
    private readonly HashSet<string> _connections = new(StringComparer.Ordinal);

    public bool HasSubscribers { get { lock (_gate) return _connections.Count != 0; } }

    public event Action<bool>? SubscriberPresenceChanged;

    public void Subscribe(string connectionId)
    {
        bool becameNonEmpty;
        lock (_gate)
        {
            var wasEmpty = _connections.Count == 0;
            becameNonEmpty = _connections.Add(connectionId) && wasEmpty;
        }
        if (becameNonEmpty) SubscriberPresenceChanged?.Invoke(true);
    }

    public void Unsubscribe(string connectionId)
    {
        bool becameEmpty;
        lock (_gate)
            becameEmpty = _connections.Remove(connectionId) && _connections.Count == 0;
        if (becameEmpty) SubscriberPresenceChanged?.Invoke(false);
    }
}
