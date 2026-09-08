using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.SystemMonitor;

/// <summary>内存占用快照（字节）。Percent = UsedBytes / TotalBytes * 100。</summary>
public sealed record MemoryUsageDto(
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("usedBytes")] long UsedBytes,
    [property: JsonPropertyName("availableBytes")] long AvailableBytes,
    [property: JsonPropertyName("percent")] double Percent);
