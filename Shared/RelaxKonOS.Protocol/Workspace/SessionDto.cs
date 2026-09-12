using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Workspace;

/// <summary>设备与 Workspace 的连接会话。对应 Authentication.md §12 session 表。Session 消失不等于 Workspace 销毁。</summary>
public sealed record SessionDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("workspaceId")] Guid WorkspaceId,
    [property: JsonPropertyName("deviceId")] Guid DeviceId,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("lastActiveAt")] DateTimeOffset LastActiveAt,
    [property: JsonPropertyName("status")] SessionStatus Status,
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("authenticationMethod")] string AuthenticationMethod,
    [property: JsonPropertyName("authenticatedAt")] DateTimeOffset AuthenticatedAt);
