using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Workspace;

/// <summary>Controller 控制权租约信息。Controller 断开后进入 Grace Period，超过 LeaseExpiresAt 释放控制权。</summary>
public sealed record ControllerLeaseInfo(
    [property: JsonPropertyName("deviceId")] Guid DeviceId,
    [property: JsonPropertyName("grantedAt")] DateTimeOffset GrantedAt,
    [property: JsonPropertyName("leaseExpiresAt")] DateTimeOffset LeaseExpiresAt);
