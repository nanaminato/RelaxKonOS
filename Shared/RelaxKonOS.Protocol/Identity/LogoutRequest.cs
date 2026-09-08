using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Identity;

/// <summary>登出请求。撤销 RefreshToken（AccessToken 自然过期）。</summary>
public sealed record LogoutRequest(
    [property: JsonPropertyName("refreshToken")] string? RefreshToken);
