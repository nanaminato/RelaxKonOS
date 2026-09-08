using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.SystemMonitor;

/// <summary>CPU 占用快照。TotalPercent 为整机利用率（0-100）；PerCorePercent 为每个逻辑核心利用率（0-100）。</summary>
public sealed record CpuUsageDto(
    [property: JsonPropertyName("totalPercent")] double TotalPercent,
    [property: JsonPropertyName("perCorePercent")] IReadOnlyList<double> PerCorePercent,
    [property: JsonPropertyName("coreCount")] int CoreCount);
