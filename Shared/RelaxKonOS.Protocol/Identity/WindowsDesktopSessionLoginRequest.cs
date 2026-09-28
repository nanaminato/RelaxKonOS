using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.Identity;

/// <summary>Device metadata for the explicit Windows Desktop integrated-session login.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WindowsDesktopSessionLoginRequest(
    [property: JsonPropertyName("clientPlatform")] ClientPlatformKind ClientPlatform,
    [property: JsonPropertyName("deviceName")] string DeviceName,
    [property: JsonPropertyName("clientVersion")] string ClientVersion);
