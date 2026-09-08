using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;

namespace RelaxKonOS.Client.Services.Auth;

/// <summary>IRelaxKonOSClient 的 typed HttpClient 实现。
/// 不 mutate HttpClient.BaseAddress，每个方法用 serverUrl 构造绝对 URI（避免共享实例并发竞态）。
/// 序列化/反序列化统一用 RelaxKonOSJsonOptions.Default；失败读 ProblemDetails 抛 RelaxKonOSAuthException。</summary>
public sealed class RelaxKonOSClient : IRelaxKonOSClient
{
    private readonly HttpClient _http;

    public RelaxKonOSClient(HttpClient http) => _http = http;

    public async Task<LoginResponse> LoginAsync(string serverUrl, LoginRequest request, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            BuildUri(serverUrl, AuthApiRoutes.Login), request, RelaxKonOSJsonOptions.Default, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<LoginResponse>(RelaxKonOSJsonOptions.Default, ct)
            ?? throw new RelaxKonOSAuthException(NoBodyProblem());
    }

    public async Task<RefreshTokenResponse> RefreshAsync(string serverUrl, string refreshToken, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            BuildUri(serverUrl, AuthApiRoutes.Refresh),
            new RefreshTokenRequest(refreshToken), RelaxKonOSJsonOptions.Default, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<RefreshTokenResponse>(RelaxKonOSJsonOptions.Default, ct)
            ?? throw new RelaxKonOSAuthException(NoBodyProblem());
    }

    public async Task LogoutAsync(string serverUrl, string accessToken, string? refreshToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, BuildUri(serverUrl, AuthApiRoutes.Logout))
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", accessToken) },
            Content = JsonContent.Create(new LogoutRequest(refreshToken), options: RelaxKonOSJsonOptions.Default),
        };
        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public async Task<UserDto> GetMeAsync(string serverUrl, string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, BuildUri(serverUrl, AuthApiRoutes.Me))
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", accessToken) },
        };
        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<UserDto>(RelaxKonOSJsonOptions.Default, ct)
            ?? throw new RelaxKonOSAuthException(NoBodyProblem());
    }

    private static Uri BuildUri(string serverUrl, string route)
    {
        var baseUri = new Uri(serverUrl, UriKind.Absolute);
        return new Uri(baseUri, route.TrimStart('/'));
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;

        ProblemDetails? problem = null;
        try { problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>(RelaxKonOSJsonOptions.Default, ct); }
        catch { /* 非 JSON 错误体，回退到通用错误 */ }

        throw problem is null
            ? new RelaxKonOSAuthException(new ProblemDetails(
                "https://relaxkonos.app/problems/http-error", $"HTTP {(int)resp.StatusCode}",
                (int)resp.StatusCode, resp.ReasonPhrase, null))
            : new RelaxKonOSAuthException(problem);
    }

    private static ProblemDetails NoBodyProblem()
        => new("https://relaxkonos.app/problems/empty-response", "空响应", 500, "服务器返回空响应体", null);
}
