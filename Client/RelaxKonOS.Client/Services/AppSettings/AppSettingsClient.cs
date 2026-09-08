using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.AppSettings;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Client.Services.AppSettings;

/// <summary>Direct client for built-in apps and host-mediated client for external app contexts.</summary>
public interface IAppSettingsClient
{
    Task<AppSettingsDocumentDto?> GetAsync(string appId, AppSettingsScope scope, string key = "default", CancellationToken cancellationToken = default);
    Task<AppSettingsDocumentDto> SaveAsync(string appId, AppSettingsScope scope, string key, JsonElement value,
        int schemaVersion = 1, long? expectedRevision = null, CancellationToken cancellationToken = default);
    Task ClearAsync(string appId, CancellationToken cancellationToken = default);
}

public sealed class AppSettingsClient(HttpClient http, IAuthSession session) : IAppSettingsClient
{
    public async Task<AppSettingsDocumentDto?> GetAsync(string appId, AppSettingsScope scope, string key = "default", CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, appId, scope, key, cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AppSettingsDocumentDto>(RemoteOsJsonOptions.Default, cancellationToken)
            ?? throw new InvalidOperationException("The server returned an empty application settings document.");
    }

    public async Task<AppSettingsDocumentDto> SaveAsync(string appId, AppSettingsScope scope, string key, JsonElement value,
        int schemaVersion = 1, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Put, appId, scope, key, cancellationToken);
        request.Content = JsonContent.Create(new PutAppSettingsRequest(value, schemaVersion), options: RemoteOsJsonOptions.Default);
        if (expectedRevision is { } revision)
            request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{revision}\""));
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AppSettingsDocumentDto>(RemoteOsJsonOptions.Default, cancellationToken)
            ?? throw new InvalidOperationException("The server returned an empty application settings document.");
    }

    public async Task ClearAsync(string appId, CancellationToken cancellationToken = default)
    {
        if (session.State != AuthSessionState.Authenticated || session.ServerUrl is null || session.Tokens is null)
            throw new InvalidOperationException("Sign in before clearing application data.");
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(new Uri(session.ServerUrl, UriKind.Absolute),
            AppSettingsApiRoutes.Application.Replace("{appId}", Uri.EscapeDataString(appId)).TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string appId, AppSettingsScope scope, string key, CancellationToken cancellationToken)
    {
        if (session.State != AuthSessionState.Authenticated || session.ServerUrl is null || session.Tokens is null)
            throw new InvalidOperationException("Sign in before using application settings.");
        var route = AppSettingsApiRoutes.Document
            .Replace("{appId}", Uri.EscapeDataString(appId))
            .Replace("{scope}", scope.ToString().ToLowerInvariant())
            .Replace("{key}", Uri.EscapeDataString(key));
        var request = new HttpRequestMessage(method, new Uri(new Uri(session.ServerUrl, UriKind.Absolute), route.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        return Task.FromResult(request);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(string.IsNullOrWhiteSpace(detail)
            ? $"Application settings request failed with HTTP {(int)response.StatusCode}."
            : detail, null, response.StatusCode);
    }
}
