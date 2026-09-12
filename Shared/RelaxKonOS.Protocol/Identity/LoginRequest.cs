using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.Identity;

/// <summary>登录请求。密码由 Server 委托宿主 OS 验证（Linux: PAM / Windows: LogonUser），RelaxKonOS 不存密码。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LoginRequest(
    [property: JsonPropertyName("identifier")] string Identifier,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("clientPlatform")] PlatformKind ClientPlatform,
    [property: JsonPropertyName("deviceName")] string DeviceName,
    [property: JsonPropertyName("clientVersion")] string ClientVersion);
