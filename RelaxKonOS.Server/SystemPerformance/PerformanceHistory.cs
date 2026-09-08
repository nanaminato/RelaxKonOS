using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.Server.SystemPerformance;

/// <summary>线程安全、固定容量的最近性能快照缓冲。仅用于短期 UI 回放，不持久化。</summary>
public sealed class PerformanceHistory
{
    private const int Capacity = 60;
    private readonly object _gate = new();
    private readonly Queue<PerformanceRealtimeSnapshotDto> _snapshots = new(Capacity);

    public void Add(PerformanceRealtimeSnapshotDto snapshot)
    {
        lock (_gate)
        {
            _snapshots.Enqueue(snapshot);
            while (_snapshots.Count > Capacity) _snapshots.Dequeue();
        }
    }

    public PerformanceRealtimeSnapshotDto? Latest()
    {
        lock (_gate) return _snapshots.Count == 0 ? null : _snapshots.Last();
    }

    public IReadOnlyList<PerformanceRealtimeSnapshotDto> GetRecent(int seconds)
    {
        var boundedSeconds = Math.Clamp(seconds, 1, Capacity);
        lock (_gate)
        {
            if (_snapshots.Count == 0) return Array.Empty<PerformanceRealtimeSnapshotDto>();
            var threshold = DateTimeOffset.UtcNow.AddSeconds(-boundedSeconds);
            return _snapshots.Where(x => x.Timestamp >= threshold).ToArray();
        }
    }
}
