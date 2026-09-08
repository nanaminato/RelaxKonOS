using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.SystemMonitor;

/// <summary>明确描述性能数据源的可用能力。false 表示不支持或当前无法可靠采集，绝不以数值 0 代替。</summary>
public sealed record PerformanceCapabilitiesDto(
    [property: JsonPropertyName("perLogicalCpu")] bool PerLogicalCpu,
    [property: JsonPropertyName("cpuFrequency")] bool CpuFrequency,
    [property: JsonPropertyName("cpuIowait")] bool CpuIowait,
    [property: JsonPropertyName("diskIo")] bool DiskIo,
    [property: JsonPropertyName("diskLatency")] bool DiskLatency,
    [property: JsonPropertyName("diskQueueLength")] bool DiskQueueLength,
    [property: JsonPropertyName("networkErrors")] bool NetworkErrors,
    [property: JsonPropertyName("gpu")] bool Gpu);

/// <summary>低频 CPU 信息。频率和虚拟化状态在无法可靠获取时为 null。</summary>
public sealed record CpuInfoDto(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("physicalCoreCount")] int? PhysicalCoreCount,
    [property: JsonPropertyName("logicalProcessorCount")] int LogicalProcessorCount,
    [property: JsonPropertyName("baseFrequencyMHz")] double? BaseFrequencyMHz,
    [property: JsonPropertyName("virtualizationEnabled")] bool? VirtualizationEnabled,
    [property: JsonPropertyName("socketCount")] int? SocketCount = null,
    [property: JsonPropertyName("l1CacheBytes")] long? L1CacheBytes = null,
    [property: JsonPropertyName("l2CacheBytes")] long? L2CacheBytes = null,
    [property: JsonPropertyName("l3CacheBytes")] long? L3CacheBytes = null);

/// <summary>低频内存信息。</summary>
public sealed record MemoryInfoDto(
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("swapTotalBytes")] long? SwapTotalBytes);

/// <summary>文件系统身份信息。容量使用情况位于 <see cref="FilesystemUsageDto"/>。</summary>
public sealed record FilesystemInfoDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mountPoint")] string MountPoint);

/// <summary>块设备身份信息。I/O 指标位于 <see cref="DiskRealtimeMetricsDto"/>。</summary>
public sealed record DiskInfoDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("filesystemIds")] IReadOnlyList<string> FilesystemIds);

/// <summary>网络接口身份信息。地址可按部署的隐私策略省略。</summary>
public sealed record NetworkInterfaceInfoDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("linkSpeedBitsPerSecond")] long? LinkSpeedBitsPerSecond,
    [property: JsonPropertyName("addresses")] IReadOnlyList<string> Addresses);

/// <summary>进入性能页时读取的低变化信息与能力。</summary>
public sealed record PerformanceInfoDto(
    [property: JsonPropertyName("cpu")] CpuInfoDto Cpu,
    [property: JsonPropertyName("memory")] MemoryInfoDto Memory,
    [property: JsonPropertyName("filesystems")] IReadOnlyList<FilesystemInfoDto> Filesystems,
    [property: JsonPropertyName("disks")] IReadOnlyList<DiskInfoDto> Disks,
    [property: JsonPropertyName("networks")] IReadOnlyList<NetworkInterfaceInfoDto> Networks,
    [property: JsonPropertyName("capabilities")] PerformanceCapabilitiesDto Capabilities);

/// <summary>CPU 的一份有效实时样本；各百分比为 0–100，未知明细为 null。</summary>
public sealed record CpuRealtimeMetricsDto(
    [property: JsonPropertyName("totalPercent")] double TotalPercent,
    [property: JsonPropertyName("userPercent")] double? UserPercent,
    [property: JsonPropertyName("systemPercent")] double? SystemPercent,
    [property: JsonPropertyName("idlePercent")] double? IdlePercent,
    [property: JsonPropertyName("iowaitPercent")] double? IowaitPercent,
    [property: JsonPropertyName("perLogicalCpuPercent")] IReadOnlyList<double> PerLogicalCpuPercent,
    [property: JsonPropertyName("currentFrequencyMHz")] double? CurrentFrequencyMHz,
    [property: JsonPropertyName("processCount")] int? ProcessCount = null,
    [property: JsonPropertyName("threadCount")] int? ThreadCount = null,
    [property: JsonPropertyName("handleCount")] long? HandleCount = null);

/// <summary>内存的一份实时样本。</summary>
public sealed record MemoryRealtimeMetricsDto(
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("usedBytes")] long UsedBytes,
    [property: JsonPropertyName("availableBytes")] long AvailableBytes,
    [property: JsonPropertyName("cachedBytes")] long? CachedBytes,
    [property: JsonPropertyName("bufferedBytes")] long? BufferedBytes,
    [property: JsonPropertyName("swapUsedBytes")] long? SwapUsedBytes,
    [property: JsonPropertyName("swapTotalBytes")] long? SwapTotalBytes);

/// <summary>用户可见文件系统的容量使用情况。</summary>
public sealed record FilesystemUsageDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("usedBytes")] long UsedBytes,
    [property: JsonPropertyName("availableBytes")] long AvailableBytes,
    [property: JsonPropertyName("percent")] double Percent);

/// <summary>单个块设备的实时 I/O 数据。不可采集的可选字段为 null。</summary>
public sealed record DiskRealtimeMetricsDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("readBytesPerSecond")] long ReadBytesPerSecond,
    [property: JsonPropertyName("writeBytesPerSecond")] long WriteBytesPerSecond,
    [property: JsonPropertyName("readIops")] double ReadIops,
    [property: JsonPropertyName("writeIops")] double WriteIops,
    [property: JsonPropertyName("activityPercent")] double? ActivityPercent,
    [property: JsonPropertyName("queueLength")] double? QueueLength,
    [property: JsonPropertyName("latencyMs")] double? LatencyMs);

/// <summary>网络接口实时计数与速率。</summary>
public sealed record NetworkRealtimeMetricsDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("bytesReceived")] long BytesReceived,
    [property: JsonPropertyName("bytesSent")] long BytesSent,
    [property: JsonPropertyName("receiveBytesPerSecond")] long ReceiveBytesPerSecond,
    [property: JsonPropertyName("sendBytesPerSecond")] long SendBytesPerSecond,
    [property: JsonPropertyName("receivePackets")] long ReceivePackets,
    [property: JsonPropertyName("sendPackets")] long SendPackets,
    [property: JsonPropertyName("receiveErrors")] long? ReceiveErrors,
    [property: JsonPropertyName("sendErrors")] long? SendErrors,
    [property: JsonPropertyName("receiveDropped")] long? ReceiveDropped,
    [property: JsonPropertyName("sendDropped")] long? SendDropped);

/// <summary>采样健康状态。Stale 表示最后有效样本已过期；Error 仅返回经脱敏的状态说明。</summary>
public sealed record PerformanceHealthDto(
    [property: JsonPropertyName("isStale")] bool IsStale,
    [property: JsonPropertyName("lastSuccessfulSampleAt")] DateTimeOffset? LastSuccessfulSampleAt,
    [property: JsonPropertyName("error")] string? Error);

/// <summary>服务端采样器生成的统一实时性能快照。Sequence 在单次服务进程生命周期内严格递增。</summary>
public sealed record PerformanceRealtimeSnapshotDto(
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("cpu")] CpuRealtimeMetricsDto Cpu,
    [property: JsonPropertyName("memory")] MemoryRealtimeMetricsDto Memory,
    [property: JsonPropertyName("filesystems")] IReadOnlyList<FilesystemUsageDto> Filesystems,
    [property: JsonPropertyName("disks")] IReadOnlyList<DiskRealtimeMetricsDto> Disks,
    [property: JsonPropertyName("networks")] IReadOnlyList<NetworkRealtimeMetricsDto> Networks,
    [property: JsonPropertyName("uptimeSeconds")] long UptimeSeconds,
    [property: JsonPropertyName("health")] PerformanceHealthDto Health);

/// <summary>分页进程查询的响应。进程实例以 PID 与 StartTime 组合识别。</summary>
public sealed record ProcessPageDto(
    [property: JsonPropertyName("items")] IReadOnlyList<ProcessInfoDto> Items,
    [property: JsonPropertyName("totalCount")] int TotalCount,
    [property: JsonPropertyName("sampledAt")] DateTimeOffset SampledAt);
