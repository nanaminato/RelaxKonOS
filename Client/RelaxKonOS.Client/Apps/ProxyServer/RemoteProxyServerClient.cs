using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.ProxyServer;

namespace RelaxKonOS.Client.Apps.ProxyServer;

/// <summary>
/// Routes are closed to the management prefix: a view model cannot turn this client into a generic
/// request proxy. A stale window can never send using another login or host.
/// </summary>
public sealed class RemoteProxyServerClient(HttpClient http, IAuthSession session) : IServerProxyServer
{
    private readonly object? _login = session.CurrentSession;
    private readonly string? _baseUrl = session.EffectiveBaseUrl;

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
        SendAsync<ProxyServerSubscriptionIssued>(HttpMethod.Post, ProxyServerRoutes.Subscriptions, request, Guid.NewGuid().ToString("N"), ct);
    public Task<ProxyOperationDto> OperationAsync(Guid id, CancellationToken ct) =>
        SendAsync<ProxyOperationDto>(HttpMethod.Get, WithId(ProxyServerRoutes.OperationPattern, id), null, null, ct);

    public Task<ProxyOperationAcceptedDto> SaveConfigurationAsync(SaveProxyServerConfiguration request, string key, CancellationToken ct) =>
        SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Put, ProxyServerRoutes.Configuration, request, key, ct);

    public Task<ProxyOperationAcceptedDto> SavePrincipalAsync(SaveProxyServerPrincipal request, string key, CancellationToken ct) =>
        request.Id is { } id
            ? SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Put, WithId(ProxyServerRoutes.PrincipalPattern, id), request, key, ct)
            : SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, ProxyServerRoutes.Principals, request, key, ct);

    public Task<ProxyOperationAcceptedDto> DeletePrincipalAsync(Guid id, string key, CancellationToken ct) =>
        SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Delete, WithId(ProxyServerRoutes.PrincipalPattern, id), new { }, key, ct);

    public Task<ProxyOperationAcceptedDto> SaveCredentialAsync(ProxyServerCredentialRequest request, string key, CancellationToken ct) =>
        request.Id is { } id
            ? SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Put, WithId(ProxyServerRoutes.CredentialPattern, id), request, key, ct)
            : SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, ProxyServerRoutes.Credentials, request, key, ct);

    public Task<ProxyOperationAcceptedDto> RotateCredentialAsync(Guid id, ProxyServerCredentialRotation request, string key, CancellationToken ct) =>
        SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, WithId(ProxyServerRoutes.CredentialRotatePattern, id), request, key, ct);

    public Task<ProxyOperationAcceptedDto> DeleteCredentialAsync(Guid id, string key, CancellationToken ct) =>
        SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Delete, WithId(ProxyServerRoutes.CredentialPattern, id), new { }, key, ct);

    public Task<ProxyOperationAcceptedDto> ApplyAsync(string key, CancellationToken ct) =>
        SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, ProxyServerRoutes.Apply, new { }, key, ct);

    public Task<ProxyOperationAcceptedDto> RollbackAsync(string key, CancellationToken ct) =>
        SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, ProxyServerRoutes.Rollback, new { }, key, ct);

    public Task<ProxyOperationAcceptedDto> RunLifecycleAsync(ProxyServerAction action, string key, CancellationToken ct)
    {
        if (action is not (ProxyServerAction.Start or ProxyServerAction.Stop or ProxyServerAction.Restart or ProxyServerAction.Enable or ProxyServerAction.Disable))
            throw new ArgumentOutOfRangeException(nameof(action));
        return SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post,
            ProxyServerRoutes.RuntimeActions + "/" + action.ToString().ToLowerInvariant(), new { }, key, ct);
    }

    public Task<ProxyOperationAcceptedDto> RevokeSubscriptionAsync(Guid id, string key, CancellationToken ct) =>
        SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Delete, WithId(ProxyServerRoutes.SubscriptionPattern, id), new { }, key, ct);

    private static string WithId(string pattern, Guid id) => pattern.Replace("{id:guid}", id.ToString("D"), StringComparison.Ordinal);

    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, string? key, CancellationToken ct)
    {
        Verify();
        var token = await session.GetAccessTokenAsync(TimeSpan.FromMinutes(1), ct: ct);
        Verify();
        using var request = new HttpRequestMessage(method, new Uri(new Uri(_baseUrl!), route.TrimStart('/')))
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
        if (_login is null || _baseUrl is null || _baseUrl != session.EffectiveBaseUrl
            || !ReferenceEquals(_login, session.CurrentSession) || session.State != AuthSessionState.Authenticated)
            throw new InvalidOperationException("proxy_server.session_changed");
    }
}
