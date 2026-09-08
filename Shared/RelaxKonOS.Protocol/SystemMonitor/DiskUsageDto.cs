using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.SystemMonitor;

/// <summary>单个磁盘/挂载点空间占用。Name 为盘符（Windows）或挂载路径（Linux）。Percent 为已用空间占比（0-100）。</summary>
public sealed record DiskUsageDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("usedBytes")] long UsedBytes,
    [property: JsonPropertyName("freeBytes")] long FreeBytes,
    [property: JsonPropertyName("percent")] double Percent);
