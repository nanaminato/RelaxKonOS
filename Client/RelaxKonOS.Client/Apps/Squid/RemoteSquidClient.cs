using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.GatewayProxy;
using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Client.Apps.Squid;

public interface ISquidClient
{
    Task<SquidConfigurationSnapshot> ReadAsync(CancellationToken ct);
    Task<SquidConfigurationPreview> PreviewAsync(SquidConfiguration configuration, CancellationToken ct);
    Task<SquidNativeValidation> ValidateAsync(SquidConfiguration configuration, CancellationToken ct);
    Task<ProxyOperationAcceptedDto> SubmitAsync(string route, object body, string key, CancellationToken ct);
    Task<ProxyOperationDto> OperationAsync(Guid id, CancellationToken ct);
}

/// <summary>One window captures its login; a stale window never sends mutations to another host.</summary>
public sealed class RemoteSquidClient(HttpClient http, IAuthSession session) : ISquidClient
{
    private readonly object? _login = session.CurrentSession;
    public Task<SquidConfigurationSnapshot> ReadAsync(CancellationToken ct) => SendAsync<SquidConfigurationSnapshot>(HttpMethod.Get, GatewayProxyRoutes.Configuration, null, null, ct);
    public Task<SquidConfigurationPreview> PreviewAsync(SquidConfiguration value, CancellationToken ct) => SendAsync<SquidConfigurationPreview>(HttpMethod.Post, GatewayProxyRoutes.Preview, value, null, ct);
    public Task<SquidNativeValidation> ValidateAsync(SquidConfiguration value, CancellationToken ct) => SendAsync<SquidNativeValidation>(HttpMethod.Post, GatewayProxyRoutes.Validate, value, null, ct);
    public Task<ProxyOperationAcceptedDto> SubmitAsync(string route, object body, string key, CancellationToken ct)
    {
        if (route is not (GatewayProxyRoutes.Apply or GatewayProxyRoutes.Lifecycle)) throw new ArgumentException("Invalid Squid route.");
        return SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, route, body, key, ct);
    }
    public Task<ProxyOperationDto> OperationAsync(Guid id, CancellationToken ct) => SendAsync<ProxyOperationDto>(HttpMethod.Get, ProxyApiRoutes.Proxy + "/operations/" + id, null, null, ct);
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
            var code = "squid.request.failed";
            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                foreach (var field in new[] { "problemCode", "title" })
                    if (json.RootElement.TryGetProperty(field, out var value) && value.GetString() is { } text) { code = text; break; }
            }
            catch (JsonException) { }
            throw new HttpRequestException(code, null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, ct) ?? throw new InvalidDataException("squid.response.invalid");
    }
    private void Verify()
    {
        if (_login is null || !ReferenceEquals(_login, session.CurrentSession) || session.State != AuthSessionState.Authenticated)
            throw new InvalidOperationException("squid.session.changed");
    }
}
