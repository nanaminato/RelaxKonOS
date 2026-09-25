using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.Server.SystemPerformance;

/// <summary>统一性能采样器的只读与订阅表面。</summary>
public interface IPerformanceSampler
{
    event Action<PerformanceRealtimeSnapshotDto>? SnapshotAvailable;

    ValueTask<PerformanceInfoDto> GetInfoAsync(CancellationToken cancellationToken = default);

    PerformanceRealtimeSnapshotDto? GetLatest();

    IReadOnlyList<PerformanceRealtimeSnapshotDto> GetHistory(int seconds);

    /// <summary>
    /// 最新有效快照；当前无人需要采样时，先申请一段有界 demand 再取样本，使一次性 REST 读取在没有
    /// 实时订阅者时同样可用。返回 null 表示在 <paramref name="wait"/> 内仍未取得有效样本。
    /// </summary>
    ValueTask<PerformanceRealtimeSnapshotDto?> ReadSnapshotAsync(TimeSpan wait, CancellationToken cancellationToken = default);
}
