using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Hubs;

/// <summary>
/// 终端会话摘要，供客户端拉取当前用户的多个终端实例。<see cref="HasExited"/> 为 true 表示 PTY 已退出，
/// 客户端不应再尝试附加（附加会改为新建）。
/// </summary>
public sealed record TerminalSessionInfo(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("hasExited")] bool HasExited);
