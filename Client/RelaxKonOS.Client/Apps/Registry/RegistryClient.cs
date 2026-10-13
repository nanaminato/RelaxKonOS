using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Protocol.Privileged;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Registry;

namespace RelaxKonOS.Client.Apps.Registry;

public sealed class RegistryClient(HttpClient http, IAuthSession session, IHostElevationBroker elevations, RelaxKonOS.Client.Services.PresentationSettingsChanges changes) : IRegistryClient
{
    private Guid? TargetUserId { get; init; }
    private string? BoundBaseUrl { get; init; }
    private Guid? BoundActor { get; init; }
    private Guid? BoundDevice { get; init; }
    public IRegistryClient ForUser(Guid targetUserId) => new RegistryClient(http, session, elevations, changes)
    { TargetUserId = targetUserId, BoundBaseUrl = session.EffectiveBaseUrl, BoundActor = session.CurrentUser?.Id, BoundDevice = session.CurrentDevice?.Id };
    public Task<RegistryContextDto> GetContextAsync(CancellationToken cancellationToken = default) => GetCoreAsync<RegistryContextDto>(RegistryApiRoutes.Context, cancellationToken);
    public Task<IReadOnlyList<RegistryUserDto>> ListUsersAsync(CancellationToken cancellationToken = default) =>
        elevations.ExecuteAsync(HostElevationCapability.RegistryManage, "registry/users",
            () => GetCoreAsync<IReadOnlyList<RegistryUserDto>>(RegistryApiRoutes.Users, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<RegistryEntryDto>> ListValuesAsync(RegistryScope scope, string path, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<RegistryEntryDto>>($"{RegistryApiRoutes.Values}?scope={Uri.EscapeDataString(scope.ToString())}&path={Uri.EscapeDataString(path)}", cancellationToken);
    public Task<IReadOnlyList<RegistryKeyDto>> ListKeysAsync(RegistryScope scope, string parentPath, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<RegistryKeyDto>>($"{RegistryApiRoutes.Keys}?scope={Uri.EscapeDataString(scope.ToString())}&path={Uri.EscapeDataString(parentPath)}", cancellationToken);
    public Task<RegistryKeyDto> CreateKeyAsync(CreateRegistryKeyRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<RegistryKeyDto>(HttpMethod.Post, RegistryApiRoutes.Keys, request, cancellationToken);
    public Task DeleteKeyAsync(RegistryScope scope, string path, CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete, $"{RegistryApiRoutes.Keys}?scope={Uri.EscapeDataString(scope.ToString())}&path={Uri.EscapeDataString(path)}", null, cancellationToken);
    public async Task<RegistryEntryDto> SaveAsync(PutRegistryEntryRequest request, CancellationToken cancellationToken = default)
    {
        var saved = await SendAsync<RegistryEntryDto>(HttpMethod.Put, RegistryApiRoutes.Values, request, cancellationToken);
        changes.Publish(saved);
        return saved;
    }
    public Task<RegistryEntryDto> RenameAsync(RenameRegistryEntryRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<RegistryEntryDto>(HttpMethod.Post, RegistryApiRoutes.RenameValue, request, cancellationToken);
    public Task DeleteAsync(RegistryScope scope, string path, string name, CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete, $"{RegistryApiRoutes.Values}?scope={Uri.EscapeDataString(scope.ToString())}&path={Uri.EscapeDataString(path)}&name={Uri.EscapeDataString(name)}", null, cancellationToken);

    private async Task<T> GetAsync<T>(string route, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cancellationToken);
        var owner = TargetUserId ?? throw new InvalidOperationException("An explicit registry owner is required.");
        var ownedRoute = OwnedRoute(route, owner);
        return owner != context.CurrentUserId
            ? await elevations.ExecuteAsync(HostElevationCapability.RegistryManage, "registry/users/" + owner.ToString("D"),
                () => GetCoreAsync<T>(ownedRoute, cancellationToken), cancellationToken)
            : await GetCoreAsync<T>(ownedRoute, cancellationToken);
    }
    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cancellationToken);
        var owner = TargetUserId ?? throw new InvalidOperationException("An explicit registry owner is required.");
        var ownedRoute = OwnedRoute(route, owner);
        return owner != context.CurrentUserId
            ? await elevations.ExecuteAsync(HostElevationCapability.RegistryManage, "registry/users/" + owner.ToString("D"),
                () => SendCoreAsync<T>(method, ownedRoute, body, cancellationToken), cancellationToken)
            : await SendCoreAsync<T>(method, ownedRoute, body, cancellationToken);
    }
    private static string OwnedRoute(string route, Guid owner) => RegistryApiRoutes.ForUser(route, owner);
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(RelaxKonOSJsonOptions.Default, cancellationToken);
        throw new RelaxKonOSAuthException(problem ?? new ProblemDetails("https://relaxkonos.app/problems/registry-failed", "Registry", (int)response.StatusCode, null, null));
    }

    private async Task<T> GetCoreAsync<T>(string route, CancellationToken cancellationToken)
    {
        EnsureBoundSession();
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.EffectiveBaseUrl is null)
            throw new InvalidOperationException(LocalizedText.Get("registry.error.sign_in", "Sign in to browse the configuration registry."));
        using var request = CreateRequest(HttpMethod.Get, route);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, cancellationToken)
            ?? throw new InvalidOperationException(LocalizedText.Get("registry.error.empty_response", "The registry server returned an empty response."));
    }
    private async Task<T> SendCoreAsync<T>(HttpMethod method, string route, object? body, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, route);
        if (body is not null) request.Content = JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        if (typeof(T) == typeof(object)) return (T)(object)new object();
        return await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, cancellationToken)
            ?? throw new InvalidOperationException(LocalizedText.Get("registry.error.empty_response", "The registry server returned an empty response."));
    }
    private void EnsureBoundSession()
    {
        if (TargetUserId is not null && (session.EffectiveBaseUrl != BoundBaseUrl || session.CurrentUser?.Id != BoundActor || session.CurrentDevice?.Id != BoundDevice))
            throw new InvalidOperationException("The registry session changed. Reopen the registry editor.");
    }
    private HttpRequestMessage CreateRequest(HttpMethod method, string route)
    {
        EnsureBoundSession();
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.EffectiveBaseUrl is null)
            throw new InvalidOperationException(LocalizedText.Get("registry.error.sign_in", "Sign in to browse the configuration registry."));
        var request = new HttpRequestMessage(method, new Uri(new Uri(session.EffectiveBaseUrl), route.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        return request;
    }
}
