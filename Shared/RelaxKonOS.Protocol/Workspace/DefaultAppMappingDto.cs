using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Workspace;

/// <summary>
/// 一条默认程序映射：将一个 URI scheme（"http"/"mailto"）或文件扩展名（".txt"/".md"）绑定到某个应用 Id。
/// 存储在 <see cref="WorkspacePreferencesDto.DefaultApps"/> 列表中，随 Workspace 持久化。
/// </summary>
public sealed record DefaultAppMappingDto(
    [property: JsonPropertyName("scheme")] string Scheme,
    [property: JsonPropertyName("appId")] string AppId);
