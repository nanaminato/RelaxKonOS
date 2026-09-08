using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.SystemMonitor;

/// <summary>单个 GPU 占用快照。字段可空：无 NVIDIA 驱动或 nvidia-smi 不可用时字段为 null。
/// 当前通过 nvidia-smi 解析（Linux/Windows 通用），非 NVIDIA GPU 暂不支持。</summary>
public sealed record GpuUsageDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("usagePercent")] double? UsagePercent,
    [property: JsonPropertyName("memoryTotalBytes")] long? MemoryTotalBytes,
    [property: JsonPropertyName("memoryUsedBytes")] long? MemoryUsedBytes,
    [property: JsonPropertyName("temperatureCelsius")] double? TemperatureCelsius);
