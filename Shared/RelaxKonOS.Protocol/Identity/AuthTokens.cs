using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Identity;

/// <summary>认证令牌对。AccessToken 用于 REST/SignalR 鉴权，RefreshToken 用于换新。</summary>
public sealed record AuthTokens(
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("refreshToken")] string RefreshToken,
    [property: JsonPropertyName("accessTokenExpiresAt")] DateTimeOffset AccessTokenExpiresAt,
    [property: JsonPropertyName("refreshTokenExpiresAt")] DateTimeOffset RefreshTokenExpiresAt);
