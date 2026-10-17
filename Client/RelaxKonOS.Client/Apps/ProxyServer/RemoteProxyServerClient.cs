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
    Task<ProxyServerStatus> StatusAsync(CancellationToken ct);
    Task<IReadOnlyList<ProxyServerRuntimeRelease>> ReleasesAsync(CancellationToken ct);
    Task<ProxyServerValidation> ValidateAsync(CancellationToken ct);
    Task<IReadOnlyList<ProxyServerExportFormatOption>> ExportFormatsAsync(Guid credentialId, CancellationToken ct);
    Task<ProxyServerExport> ExportAsync(Guid credentialId, string format, CancellationToken ct);
    Task<ProxyServerSubscriptionIssued> IssueSubscriptionAsync(IssueProxyServerSubscription request, CancellationToken ct);
    Task RevokeSubscriptionAsync(Guid id, CancellationToken ct);
    Task<ProxyOperationAcceptedDto> SubmitAsync(HttpMethod method, string route, object body, string key, CancellationToken ct);
    Task<ProxyOperationDto> OperationAsync(Guid id, CancellationToken ct);
}

/// <summary>
/// Routes are closed to the management prefix: a view model cannot turn this client into a generic
/// request proxy. A stale window can never send using another login or host.
/// </summary>
public sealed class RemoteProxyServerClient(HttpClient http, IAuthSession session) : IProxyServerClient
{
    private readonly object? _login = session.CurrentSession;

    public Task<ProxyServerSnapshot> ReadAsync(CancellationToken ct) =>
        SendAsync<ProxyServerSnapshot>(HttpMethod.Get, ProxyServerRoutes.Configuration, null, null, ct);
    public Task<ProxyServerStatus> StatusAsync(CancellationToken ct) =>
        SendAsync<ProxyServerStatus>(HttpMethod.Get, ProxyServerRoutes.Status, null, null, ct);
    public Task<IReadOnlyList<ProxyServerRuntimeRelease>> ReleasesAsync(CancellationToken ct) =>
        SendAsync<IReadOnlyList<ProxyServerRuntimeRelease>>(HttpMethod.Get, ProxyServerRoutes.RuntimeReleases, null, null, ct);
    public Task<ProxyServerValidation> ValidateAsync(CancellationToken ct) =>
        SendAsync<ProxyServerValidation>(HttpMethod.Post, ProxyServerRoutes.Validate, new { }, null, ct);
    public Task<IReadOnlyList<ProxyServerExportFormatOption>> ExportFormatsAsync(Guid credentialId, CancellationToken ct) =>
        SendAsync<IReadOnlyList<ProxyServerExportFormatOption>>(HttpMethod.Get, WithId(ProxyServerRoutes.CredentialExportFormatsPattern, credentialId), null, null, ct);
    public Task<ProxyServerExport> ExportAsync(Guid credentialId, string format, CancellationToken ct) =>
        SendAsync<ProxyServerExport>(HttpMethod.Post, WithId(ProxyServerRoutes.CredentialExportPattern, credentialId),
            new ProxyServerExportRequest(credentialId, format), null, ct);
    public Task<ProxyServerSubscriptionIssued> IssueSubscriptionAsync(IssueProxyServerSubscription request, CancellationToken ct) =>
        SendAsync<ProxyServerSubscriptionIssued>(HttpMethod.Post, ProxyServerRoutes.Subscriptions, request, null, ct);
    public Task RevokeSubscriptionAsync(Guid id, CancellationToken ct) =>
        SendAsync<bool>(HttpMethod.Delete, WithId(ProxyServerRoutes.SubscriptionPattern, id), null, null, ct);
    public Task<ProxyOperationDto> OperationAsync(Guid id, CancellationToken ct) =>
        SendAsync<ProxyOperationDto>(HttpMethod.Get, WithId(ProxyServerRoutes.OperationPattern, id), null, null, ct);

    public Task<ProxyOperationAcceptedDto> SubmitAsync(HttpMethod method, string route, object body, string key, CancellationToken ct)
    {
        if (!route.StartsWith(ProxyServerRoutes.Root + "/", StringComparison.Ordinal)
            || route.Contains('{', StringComparison.Ordinal) || route.Length > 256)
            throw new ArgumentException(nameof(route));
        return SendAsync<ProxyOperationAcceptedDto>(method, route, body, key, ct);
    }

    private static string WithId(string pattern, Guid id) => pattern.Replace("{id:guid}", id.ToString("D"), StringComparison.Ordinal);

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
                if (json.RootElement.TryGetProperty("title", out var title) && !string.IsNullOrWhiteSpace(title.GetString()))
                    problem = title.GetString()!;
                else if (json.RootElement.TryGetProperty("problemCode", out var code) && !string.IsNullOrWhiteSpace(code.GetString()))
                    problem = code.GetString()!;
            }
            catch (JsonException) { }
            throw new HttpRequestException(problem, null, response.StatusCode);
        }
        if (typeof(T) == typeof(bool)) return (T)(object)true;
        return await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, ct)
            ?? throw new InvalidDataException("proxy_server.response_invalid");
    }

    private void Verify()
    {
        if (_login is null || !ReferenceEquals(_login, session.CurrentSession) || session.State != AuthSessionState.Authenticated)
            throw new InvalidOperationException("proxy_server.session_changed");
    }
}
