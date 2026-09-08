using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Hubs;

/// <summary>加入 Workspace 的请求。AsObserverIfTaken=true 表示当前已有 Controller 时以 Observer 身份加入。</summary>
public sealed record JoinWorkspaceRequest(
    [property: JsonPropertyName("workspaceId")] Guid WorkspaceId,
    [property: JsonPropertyName("asObserverIfTaken")] bool AsObserverIfTaken);
