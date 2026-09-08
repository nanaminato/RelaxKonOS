using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.SystemMonitor;

/// <summary>整机资源占用聚合快照。由服务端 <c>ISystemMetricsProvider.GetMetricsAsync</c> 单次采样组装。
/// 所有字段基于宿主 OS 实时读取（CPU/内存平台特定，磁盘/网络跨平台 API，GPU 经 nvidia-smi）。</summary>
public sealed record SystemMetricsDto(
    [property: JsonPropertyName("cpu")] CpuUsageDto Cpu,
    [property: JsonPropertyName("memory")] MemoryUsageDto Memory,
    [property: JsonPropertyName("disks")] IReadOnlyList<DiskUsageDto> Disks,
    [property: JsonPropertyName("networks")] IReadOnlyList<NetworkUsageDto> Networks,
    [property: JsonPropertyName("gpus")] IReadOnlyList<GpuUsageDto> Gpus,
    [property: JsonPropertyName("uptimeSeconds")] long UptimeSeconds,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp);
