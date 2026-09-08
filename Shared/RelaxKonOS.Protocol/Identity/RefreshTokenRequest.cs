using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Identity;

/// <summary>用 RefreshToken 换取新的 Access/Refresh Token 对。</summary>
public sealed record RefreshTokenRequest(
    [property: JsonPropertyName("refreshToken")] string RefreshToken);
