using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Hubs;

/// <summary>控制权变更事件参数。由 Controller 转移、Grace Period 超时、主动 Release 触发。</summary>
public sealed record ControllerChangedEventArgs(
    [property: JsonPropertyName("workspaceId")] Guid WorkspaceId,
    [property: JsonPropertyName("previousControllerDeviceId")] Guid? PreviousControllerDeviceId,
    [property: JsonPropertyName("newControllerDeviceId")] Guid? NewControllerDeviceId,
    [property: JsonPropertyName("reason")] string Reason);
