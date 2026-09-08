using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Hubs;

/// <summary>
/// <see cref="TerminalHubMethods.Start"/> 的返回值。无论新建还是附加到既有会话都返回当前会话 ID，
/// 客户端据此知道自己是恢复了已有终端（<see cref="Created"/> == false）还是创建了新终端。
/// </summary>
public sealed record AttachTerminalResponse(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("created")] bool Created);
