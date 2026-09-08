using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Workspace;

/// <summary>访问 Workspace 的终端设备。对应 Authentication.md §13 device 表。</summary>
public sealed record DeviceDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("clientVersion")] string ClientVersion,
    [property: JsonPropertyName("lastLoginAt")] DateTimeOffset? LastLoginAt);
