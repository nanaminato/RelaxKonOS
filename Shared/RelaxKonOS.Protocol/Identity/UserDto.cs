using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.Identity;

/// <summary>RelaxKonOS 用户身份。对应 Authentication.md §10 users 表。RelaxKonOS 不保存宿主 OS 密码，认证委托宿主 OS。</summary>
public sealed record UserDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("platform")] PlatformKind Platform,
    [property: JsonPropertyName("platformIdentity")] string PlatformIdentity,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("lastLoginAt")] DateTimeOffset? LastLoginAt);
