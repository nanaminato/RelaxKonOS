using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.ProxyServer;

namespace RelaxKonOS.Client.Apps.ProxyServer;

public interface IProxyServerClient
{
    Task<ProxyServerSnapshot> ReadAsync(CancellationToken ct);
    Task<ProxyOperationAcceptedDto> SubmitAsync(string route, object body, string key, CancellationToken ct);
    Task<ProxyOperationDto> OperationAsync(Guid id, CancellationToken ct);
    Task<ProxyServerValidation> ValidateAsync(CancellationToken ct);
    Task<ProxyServerExport> ExportAsync(ProxyServerExportRequest request, CancellationToken ct);
}

/// <summary>A stale window cannot send requests using another login or host.</summary>
public sealed class RemoteProxyServerClient(HttpClient http, IAuthSession session) : IProxyServerClient
{
    private readonly object? _login = session.CurrentSession;
    public Task<ProxyServerSnapshot> ReadAsync(CancellationToken ct) => SendAsync<ProxyServerSnapshot>(HttpMethod.Get, ProxyServerRoutes.Configuration, null, null, ct);
    public Task<ProxyOperationDto> OperationAsync(Guid id, CancellationToken ct) => SendAsync<ProxyOperationDto>(HttpMethod.Get, ProxyApiRoutes.Proxy + "/operations/" + id, null, null, ct);
    public Task<ProxyServerValidation> ValidateAsync(CancellationToken ct) => SendAsync<ProxyServerValidation>(HttpMethod.Post, ProxyServerRoutes.Validate, new { }, null, ct);
    public Task<ProxyServerExport> ExportAsync(ProxyServerExportRequest request, CancellationToken ct) => SendAsync<ProxyServerExport>(HttpMethod.Post, ProxyServerRoutes.Export, request, null, ct);
    public Task<ProxyOperationAcceptedDto> SubmitAsync(string route, object body, string key, CancellationToken ct)
    {
        if (route is not (ProxyServerRoutes.Configuration or ProxyServerRoutes.Apply or ProxyServerRoutes.Credentials or ProxyServerRoutes.Runtime)) throw new ArgumentException(nameof(route));
        return SendAsync<ProxyOperationAcceptedDto>(route == ProxyServerRoutes.Configuration ? HttpMethod.Put : HttpMethod.Post, route, body, key, ct);
    }
    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, string? key, CancellationToken ct)
    {
        Verify();
        var token = await session.GetAccessTokenAsync(TimeSpan.FromMinutes(1), ct: ct);
        Verify();
        using var request = new HttpRequestMessage(method, new Uri(new Uri(session.EffectiveBaseUrl!), route.TrimStart('/')))
        { Content = body is null ? null : JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        using var response = await http.SendAsync(request, ct);
        Verify();
        if (!response.IsSuccessStatusCode)
        {
            var problem = "proxy_server.request_failed";
            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (json.RootElement.TryGetProperty("title", out var title)) problem = title.GetString() ?? problem;
            }
            catch (JsonException) { }
            throw new HttpRequestException(problem, null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, ct) ?? throw new InvalidDataException("proxy_server.response_invalid");
    }
    private void Verify()
    {
        if (_login is null || !ReferenceEquals(_login, session.CurrentSession) || session.State != AuthSessionState.Authenticated)
            throw new InvalidOperationException("proxy_server.session_changed");
    }
}
