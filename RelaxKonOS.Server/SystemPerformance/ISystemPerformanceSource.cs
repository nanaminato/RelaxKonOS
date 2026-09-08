using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.Server.SystemPerformance;

/// <summary>
/// 平台性能原始数据源。实现只读取当前 OS 状态；差分、速率、历史和推送全部由
/// <see cref="PerformanceSampler"/> 统一处理，不能保存在此接口实现中。
/// </summary>
public interface ISystemPerformanceSource
{
    ValueTask<PerformanceInfoDto> GetInfoAsync(CancellationToken cancellationToken = default);

    ValueTask<RawPerformanceSample> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>尚未差分的 OS 计数器。计数单位只需在同一数据源的连续样本内一致。</summary>
public sealed record RawPerformanceSample(
    DateTimeOffset Timestamp,
    long MonotonicTimestamp,
    RawCpuTimes Cpu,
    RawMemory Memory,
    IReadOnlyList<RawFilesystemUsage> Filesystems,
    IReadOnlyList<RawDiskCounters> Disks,
    IReadOnlyList<RawNetworkCounters> Networks,
    long UptimeSeconds);

public sealed record RawCpuTimes(
    long Total,
    long Idle,
    long? User,
    long? System,
    long? Iowait,
    IReadOnlyList<RawCpuTimes> LogicalProcessors,
    double? CurrentFrequencyMHz,
    int? ProcessCount = null,
    int? ThreadCount = null,
    long? HandleCount = null);

public sealed record RawMemory(
    long TotalBytes,
    long AvailableBytes,
    long? CachedBytes,
    long? BufferedBytes,
    long? SwapTotalBytes,
    long? SwapAvailableBytes);

public sealed record RawFilesystemUsage(string Id, long TotalBytes, long AvailableBytes);

public sealed record RawDiskCounters(
    string Id,
    long ReadOperations,
    long WriteOperations,
    long ReadSectors,
    long WriteSectors,
    long BusyMilliseconds,
    long? QueueMilliseconds,
    long? ReadMilliseconds,
    long? WriteMilliseconds,
    int SectorSizeBytes);

public sealed record RawNetworkCounters(
    string Id,
    long BytesReceived,
    long BytesSent,
    long ReceivePackets,
    long SendPackets,
    long? ReceiveErrors,
    long? SendErrors,
    long? ReceiveDropped,
    long? SendDropped);
