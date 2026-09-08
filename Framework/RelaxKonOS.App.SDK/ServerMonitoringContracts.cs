namespace RelaxKonOS.AppSDK;

/// <summary>Permission-gated, read-only server monitoring capability for package applications.</summary>
public interface IServerMonitor
{
    Task<ServerMetricsResult> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams host-polled snapshots. The host clamps the requested interval to protect the server
    /// from excessive sampling; every update also carries a status instead of throwing on denial.
    /// </summary>
    IAsyncEnumerable<ServerMetricsResult> WatchAsync(TimeSpan? interval = null, CancellationToken cancellationToken = default);
}

public sealed record ServerMetricsResult(AppCapabilityResult Status, ServerMetricsSnapshot? Snapshot);

/// <summary>Stable SDK representation of aggregate server resource usage.</summary>
public sealed record ServerMetricsSnapshot(
    DateTimeOffset Timestamp,
    double CpuPercent,
    int CpuCoreCount,
    IReadOnlyList<double> CpuPerCorePercent,
    long MemoryTotalBytes,
    long MemoryUsedBytes,
    long MemoryAvailableBytes,
    double MemoryPercent,
    IReadOnlyList<ServerDiskMetric> Disks,
    IReadOnlyList<ServerNetworkMetric> Networks,
    IReadOnlyList<ServerGpuMetric> Gpus,
    long UptimeSeconds);

public sealed record ServerDiskMetric(string Name, long TotalBytes, long UsedBytes, long FreeBytes, double Percent);
public sealed record ServerNetworkMetric(string Name, long SendRateBytesPerSecond, long ReceiveRateBytesPerSecond);
public sealed record ServerGpuMetric(string Name, double? UsagePercent, long? MemoryTotalBytes, long? MemoryUsedBytes, double? TemperatureCelsius);
