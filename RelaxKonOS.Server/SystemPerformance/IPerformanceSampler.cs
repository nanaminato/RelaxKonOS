using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.Server.SystemPerformance;

/// <summary>统一性能采样器的只读与订阅表面。</summary>
public interface IPerformanceSampler
{
    event Action<PerformanceRealtimeSnapshotDto>? SnapshotAvailable;

    ValueTask<PerformanceInfoDto> GetInfoAsync(CancellationToken cancellationToken = default);

    PerformanceRealtimeSnapshotDto? GetLatest();

    IReadOnlyList<PerformanceRealtimeSnapshotDto> GetHistory(int seconds);
}
