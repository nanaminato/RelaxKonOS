using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.SystemMonitor;

/// <summary>单个进程信息。CpuPercent 由服务端相邻采样差分计算（相对整机，0-100）。
/// UserName 为进程属主（Linux 解析 /proc uid→用户名；Windows 暂留 null）。</summary>
public sealed record ProcessInfoDto(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("cpuPercent")] double CpuPercent,
    [property: JsonPropertyName("memoryBytes")] long MemoryBytes,
    [property: JsonPropertyName("userName")] string? UserName,
    [property: JsonPropertyName("startTime")] DateTimeOffset? StartTime,
    [property: JsonPropertyName("threadCount")] int ThreadCount);
