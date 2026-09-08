using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Desktop;

namespace RelaxKonOS.Protocol.Workspace;

/// <summary>Join Workspace 时返回的全量快照：Workspace 元信息 + 活跃 Session 列表 + 在线设备 + 当前桌面状态 + 当前设备角色。</summary>
public sealed record WorkspaceSnapshotDto(
    [property: JsonPropertyName("workspace")] WorkspaceDto Workspace,
    [property: JsonPropertyName("sessions")] IReadOnlyList<SessionDto> Sessions,
    [property: JsonPropertyName("devices")] IReadOnlyList<DeviceDto> Devices,
    [property: JsonPropertyName("desktopState")] DesktopStateDto DesktopState,
    [property: JsonPropertyName("currentRole")] DeviceRole CurrentRole);
