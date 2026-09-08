using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.SystemMonitor;

/// <summary>结束进程结果。Success 表示是否已成功终止；RequiresElevation 为 true 表示服务端当前身份权限不足
/// （符合 RelaxKonOS 硬约束：权限提升委托宿主 OS，RelaxKonOS 不存储宿主密码、不自动提权——
/// 用户需在宿主 OS 提升权限，例如通过终端 sudo kill / UAC 运行）。</summary>
public sealed record KillProcessResultDto(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("requiresElevation")] bool RequiresElevation,
    [property: JsonPropertyName("error")] string? Error);
