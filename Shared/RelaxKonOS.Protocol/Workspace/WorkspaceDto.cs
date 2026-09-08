using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Workspace;

/// <summary>RelaxKonOS 用户环境。一个 User 默认拥有一个持久 Workspace。对应 Authentication.md §11 workspace 表。</summary>
public sealed record WorkspaceDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("state")] WorkspaceState State,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("controller")] ControllerLeaseInfo? Controller);
