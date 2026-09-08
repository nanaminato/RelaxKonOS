using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;

namespace RelaxKonOS.Client.Services.Auth;

/// <summary>IRemoteOsClient 的 typed HttpClient 实现。
/// 不 mutate HttpClient.BaseAddress，每个方法用 serverUrl 构造绝对 URI（避免共享实例并发竞态）。
/// 序列化/反序列化统一用 RemoteOsJsonOptions.Default；失败读 ProblemDetails 抛 RemoteOsAuthException。</summary>
public sealed class RemoteOsClient : IRemoteOsClient
{
    private readonly HttpClient _http;

    public RemoteOsClient(HttpClient http) => _http = http;

    public async Task<LoginResponse> LoginAsync(string serverUrl, LoginRequest request, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            BuildUri(serverUrl, AuthApiRoutes.Login), request, RemoteOsJsonOptions.Default, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<LoginResponse>(RemoteOsJsonOptions.Default, ct)
            ?? throw new RemoteOsAuthException(NoBodyProblem());
    }

    public async Task<RefreshTokenResponse> RefreshAsync(string serverUrl, string refreshToken, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            BuildUri(serverUrl, AuthApiRoutes.Refresh),
            new RefreshTokenRequest(refreshToken), RemoteOsJsonOptions.Default, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<RefreshTokenResponse>(RemoteOsJsonOptions.Default, ct)
            ?? throw new RemoteOsAuthException(NoBodyProblem());
    }

    public async Task LogoutAsync(string serverUrl, string accessToken, string? refreshToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, BuildUri(serverUrl, AuthApiRoutes.Logout))
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", accessToken) },
            Content = JsonContent.Create(new LogoutRequest(refreshToken), options: RemoteOsJsonOptions.Default),
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
        return await resp.Content.ReadFromJsonAsync<UserDto>(RemoteOsJsonOptions.Default, ct)
            ?? throw new RemoteOsAuthException(NoBodyProblem());
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
        try { problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>(RemoteOsJsonOptions.Default, ct); }
        catch { /* 非 JSON 错误体，回退到通用错误 */ }

        throw problem is null
            ? new RemoteOsAuthException(new ProblemDetails(
                "https://remoteos.app/problems/http-error", $"HTTP {(int)resp.StatusCode}",
                (int)resp.StatusCode, resp.ReasonPhrase, null))
            : new RemoteOsAuthException(problem);
    }

    private static ProblemDetails NoBodyProblem()
        => new("https://remoteos.app/problems/empty-response", "空响应", 500, "服务器返回空响应体", null);
}
