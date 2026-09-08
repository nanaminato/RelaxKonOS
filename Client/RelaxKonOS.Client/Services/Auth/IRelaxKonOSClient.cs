using RelaxKonOS.Protocol.Identity;

namespace RelaxKonOS.Client.Services.Auth;

/// <summary>RelaxKonOS Server HTTP 客户端抽象。typed HttpClient 实现（见 RelaxKonOSClient）。
/// 所有方法接收 serverUrl 构造绝对 URI，避免共享 HttpClient 实例 mutate BaseAddress 的并发竞态。</summary>
public interface IRelaxKonOSClient
{
    /// <summary>登录。serverUrl 形如 "http://localhost:5090"。</summary>
    Task<LoginResponse> LoginAsync(string serverUrl, LoginRequest request, CancellationToken ct = default);

    /// <summary>用 RefreshToken 换取新的令牌对。</summary>
    Task<RefreshTokenResponse> RefreshAsync(string serverUrl, string refreshToken, CancellationToken ct = default);

    /// <summary>登出（吊销 RefreshToken）。accessToken 通过 Authorization 头携带。</summary>
    Task LogoutAsync(string serverUrl, string accessToken, string? refreshToken, CancellationToken ct = default);

    /// <summary>当前用户信息（需 JWT）。</summary>
    Task<UserDto> GetMeAsync(string serverUrl, string accessToken, CancellationToken ct = default);
}
