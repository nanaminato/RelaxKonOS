using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Protocol.Hubs;

/// <summary>设备上下线事件参数。设备 Join/Leave Workspace Group 时广播给同 Workspace 其他设备。</summary>
public sealed record DevicePresenceEventArgs(
    [property: JsonPropertyName("workspaceId")] Guid WorkspaceId,
    [property: JsonPropertyName("device")] DeviceDto Device,
    [property: JsonPropertyName("connected")] bool Connected);
