using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Workspace;

/// <summary>创建 Workspace 请求。未来支持一个 User 多 Workspace。</summary>
public sealed record CreateWorkspaceRequest(
    [property: JsonPropertyName("name")] string Name);
