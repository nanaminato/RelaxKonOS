using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.Workspace;

/// <summary>显式注册设备请求（登录时通常已隐式注册）。</summary>
public sealed record RegisterDeviceRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("platform")] PlatformKind Platform,
    [property: JsonPropertyName("clientVersion")] string ClientVersion);
