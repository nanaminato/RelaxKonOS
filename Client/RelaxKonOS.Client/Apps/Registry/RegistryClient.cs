using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Registry;

namespace RelaxKonOS.Client.Apps.Registry;

public sealed class RegistryClient(HttpClient http, IAuthSession session) : IRegistryClient
{
    public Task<IReadOnlyList<RegistryEntryDto>> ListValuesAsync(RegistryScope scope, string path, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<RegistryEntryDto>>($"{RegistryApiRoutes.Entries}?scope={Uri.EscapeDataString(scope.ToString())}&path={Uri.EscapeDataString(path)}", cancellationToken);
    public Task<IReadOnlyList<RegistryKeyDto>> ListKeysAsync(RegistryScope scope, string parentPath, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<RegistryKeyDto>>($"{RegistryApiRoutes.Keys}?scope={Uri.EscapeDataString(scope.ToString())}&path={Uri.EscapeDataString(parentPath)}", cancellationToken);
    public Task<RegistryKeyDto> CreateKeyAsync(CreateRegistryKeyRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<RegistryKeyDto>(HttpMethod.Post, RegistryApiRoutes.Keys, request, cancellationToken);
    public Task DeleteKeyAsync(RegistryScope scope, string path, CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete, $"{RegistryApiRoutes.Keys}?scope={Uri.EscapeDataString(scope.ToString())}&path={Uri.EscapeDataString(path)}", null, cancellationToken);
    public Task<RegistryEntryDto> SaveAsync(PutRegistryEntryRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<RegistryEntryDto>(HttpMethod.Put, RegistryApiRoutes.Entries, request, cancellationToken);
    public Task DeleteAsync(RegistryScope scope, string path, string name, CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete, $"{RegistryApiRoutes.Entries}?scope={Uri.EscapeDataString(scope.ToString())}&path={Uri.EscapeDataString(path)}&name={Uri.EscapeDataString(name)}", null, cancellationToken);

    private async Task<T> GetAsync<T>(string route, CancellationToken cancellationToken)
    {
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.ServerUrl is null)
            throw new InvalidOperationException(LocalizedText.Get("registry.error.sign_in", "Sign in to browse the configuration registry."));
        using var request = CreateRequest(HttpMethod.Get, route);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(RemoteOsJsonOptions.Default, cancellationToken)
            ?? throw new InvalidOperationException(LocalizedText.Get("registry.error.empty_response", "The registry server returned an empty response."));
    }
    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, route);
        if (body is not null) request.Content = JsonContent.Create(body, options: RemoteOsJsonOptions.Default);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (typeof(T) == typeof(object)) return (T)(object)new object();
        return await response.Content.ReadFromJsonAsync<T>(RemoteOsJsonOptions.Default, cancellationToken)
            ?? throw new InvalidOperationException(LocalizedText.Get("registry.error.empty_response", "The registry server returned an empty response."));
    }
    private HttpRequestMessage CreateRequest(HttpMethod method, string route)
    {
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.ServerUrl is null)
            throw new InvalidOperationException(LocalizedText.Get("registry.error.sign_in", "Sign in to browse the configuration registry."));
        var request = new HttpRequestMessage(method, new Uri(new Uri(session.ServerUrl), route.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        return request;
    }
}
