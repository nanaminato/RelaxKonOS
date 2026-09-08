using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Client.Apps.Proxy;

/// <summary>Typed RelaxKonOS API client. It never receives a Mihomo controller address, secret or response schema.</summary>
public sealed class RemoteProxyRepository(HttpClient http, IAuthSession session) : IProxyRepository
{
    public Task<ProxyOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default) => SendAsync<ProxyOverviewDto>(HttpMethod.Get, ProxyApiRoutes.Overview, null, cancellationToken);
    public Task<IReadOnlyList<ProxyProfileDto>> ListProfilesAsync(CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<ProxyProfileDto>>(HttpMethod.Get, ProxyApiRoutes.Profiles, null, cancellationToken);
    public Task<IReadOnlyList<ProxySubscriptionDto>> ListSubscriptionsAsync(CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<ProxySubscriptionDto>>(HttpMethod.Get, ProxyApiRoutes.Subscriptions, null, cancellationToken);
    public Task<ProxySubscriptionDownloadOptionsDto> GetSubscriptionDownloadOptionsAsync(CancellationToken cancellationToken = default) => SendAsync<ProxySubscriptionDownloadOptionsDto>(HttpMethod.Get, ProxyApiRoutes.SubscriptionDownloadOptions, null, cancellationToken);
    public Task<ProxySubscriptionDto> ImportSubscriptionAsync(ImportProxySubscriptionRequest request, CancellationToken cancellationToken = default) => SendAsync<ProxySubscriptionDto>(HttpMethod.Post, ProxyApiRoutes.Subscriptions, request, cancellationToken);
    public Task<ProxySubscriptionContentDto> GetSubscriptionContentAsync(Guid subscriptionId, CancellationToken cancellationToken = default) => SendAsync<ProxySubscriptionContentDto>(HttpMethod.Get, SubscriptionRoute(subscriptionId) + "/content", null, cancellationToken);
    public Task<ProxyOperationAcceptedDto> RefreshAllSubscriptionsAsync(CancellationToken cancellationToken = default) => SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, ProxyApiRoutes.SubscriptionsRefresh, null, cancellationToken, idempotent: true);
    public Task<ProxyOperationAcceptedDto> ActivateSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default) => SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, SubscriptionRoute(subscriptionId) + "/activate", null, cancellationToken, idempotent: true);
    public Task<IReadOnlyList<ProxyGroupDto>> ListGroupsAsync(CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<ProxyGroupDto>>(HttpMethod.Get, ProxyApiRoutes.Groups, null, cancellationToken);
    public Task<ProxyRoutingModeDto> GetRoutingModeAsync(CancellationToken cancellationToken = default) => SendAsync<ProxyRoutingModeDto>(HttpMethod.Get, ProxyApiRoutes.Routing, null, cancellationToken);
    public Task SetRoutingModeAsync(ProxyRoutingMode mode, CancellationToken cancellationToken = default) => SendNoContentAsync(HttpMethod.Put, ProxyApiRoutes.Routing, new ProxyRoutingModeDto(mode), cancellationToken);
    public Task<ProxyDelayDto> TestProxyDelayAsync(string groupName, string proxyName, TestProxyDelayRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<ProxyDelayDto>(HttpMethod.Post, ProxyApiRoutes.Proxy + "/groups/" + Uri.EscapeDataString(groupName) + "/proxies/" + Uri.EscapeDataString(proxyName) + "/delay", request, cancellationToken);
    public Task<IReadOnlyList<ProxyConnectionDto>> ListConnectionsAsync(CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<ProxyConnectionDto>>(HttpMethod.Get, ProxyApiRoutes.Connections, null, cancellationToken);
    public Task<ProxyTrafficDto> GetTrafficAsync(CancellationToken cancellationToken = default) => SendAsync<ProxyTrafficDto>(HttpMethod.Get, ProxyApiRoutes.Traffic, null, cancellationToken);
    public Task<IReadOnlyList<ProxyLogEntryDto>> ListLogsAsync(int limit = 200, CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<ProxyLogEntryDto>>(HttpMethod.Get, ProxyApiRoutes.Logs + "?limit=" + Math.Clamp(limit, 1, 500), null, cancellationToken);
    public Task<ProxyDnsStatusDto> GetDnsStatusAsync(CancellationToken cancellationToken = default) => SendAsync<ProxyDnsStatusDto>(HttpMethod.Get, ProxyApiRoutes.Dns, null, cancellationToken);
    public Task<ProxySettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) => SendAsync<ProxySettingsDto>(HttpMethod.Get, ProxyApiRoutes.Settings, null, cancellationToken);
    public Task UpdateSettingsAsync(UpdateProxySettingsRequest request, CancellationToken cancellationToken = default) => SendNoContentAsync(HttpMethod.Put, ProxyApiRoutes.Settings, request, cancellationToken);
    public Task<ProxyGeoDataDto> GetGeoDataAsync(CancellationToken cancellationToken = default) => SendAsync<ProxyGeoDataDto>(HttpMethod.Get, ProxyApiRoutes.GeoData, null, cancellationToken);
    public Task ConfigureGeoDataFromServerFileAsync(string filePath, CancellationToken cancellationToken = default) => SendNoContentAsync(HttpMethod.Put, ProxyApiRoutes.GeoData, new ConfigureProxyGeoDataRequest(filePath), cancellationToken);
    public Task<ProxyRuntimeDto> GetRuntimeAsync(CancellationToken cancellationToken = default) => SendAsync<ProxyRuntimeDto>(HttpMethod.Get, ProxyApiRoutes.Runtime, null, cancellationToken);
    public Task<ProxyRuntimeDownloadDto?> GetManagedRuntimeDownloadAsync(string? version = null, CancellationToken cancellationToken = default) =>
        SendAsync<ProxyRuntimeDownloadDto?>(HttpMethod.Get, ProxyApiRoutes.RuntimeDownload + (string.IsNullOrWhiteSpace(version) ? string.Empty : "?version=" + Uri.EscapeDataString(version)), null, cancellationToken);
    public Task<ProxyOperationDto?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default) => SendAsync<ProxyOperationDto?>(HttpMethod.Get, ProxyApiRoutes.Proxy + "/operations/" + operationId.ToString("D"), null, cancellationToken);
    public Task<ProxyOperationAcceptedDto> LifecycleAsync(ProxyLifecycleAction action, CancellationToken cancellationToken = default) => SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, ProxyApiRoutes.Lifecycle.Replace("{action}", action.ToString().ToLowerInvariant()), new ProxyLifecycleRequest(true), cancellationToken, idempotent: true);
    public Task<ProxyOperationAcceptedDto> InstallRuntimeAsync(string engineId, string? version = null, CancellationToken cancellationToken = default) => SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, ProxyApiRoutes.RuntimeInstall, new ProxyRuntimeRequest(engineId, version), cancellationToken, idempotent: true);
    public Task<ProxyOperationAcceptedDto> InstallRuntimeFromServerFileAsync(string engineId, string archivePath, string? version = null, CancellationToken cancellationToken = default) => SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, ProxyApiRoutes.RuntimeInstallFromFile, new InstallProxyRuntimeFromFileRequest(engineId, version, archivePath), cancellationToken, idempotent: true);
    public Task<ProxyOperationAcceptedDto> RollbackRuntimeAsync(CancellationToken cancellationToken = default) => SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, ProxyApiRoutes.RuntimeRollback, null, cancellationToken, idempotent: true);
    public Task<ProxyOperationAcceptedDto> UninstallRuntimeAsync(CancellationToken cancellationToken = default) => SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Delete, ProxyApiRoutes.RuntimeUninstall, null, cancellationToken, idempotent: true);
    public Task<ProxyOperationAcceptedDto> EnableTunAsync(Guid profileId, CancellationToken cancellationToken = default) => SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, ProxyApiRoutes.TunEnable, new ProxyTunRequest(profileId), cancellationToken, idempotent: true);
    public Task<ProxyOperationAcceptedDto> DisableTunAsync(CancellationToken cancellationToken = default) => SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, ProxyApiRoutes.TunDisable, null, cancellationToken, idempotent: true);
    public Task<ProxyOperationAcceptedDto> EmergencyDisableTunAsync(CancellationToken cancellationToken = default) => SendAsync<ProxyOperationAcceptedDto>(HttpMethod.Post, ProxyApiRoutes.TunEmergencyDisable, null, cancellationToken, idempotent: true);
    public Task<ProxyProfileDto> CreateProfileAsync(string name, string engineId, CancellationToken cancellationToken = default) => SendAsync<ProxyProfileDto>(HttpMethod.Post, ProxyApiRoutes.Profiles, new UpsertProxyProfileRequest(name, engineId), cancellationToken);
    public Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default) => SendNoContentAsync(HttpMethod.Delete, ProfileRoute(profileId), null, cancellationToken);
    public Task<ProxyProfileDto> ActivateProfileAsync(Guid profileId, CancellationToken cancellationToken = default) => SendAsync<ProxyProfileDto>(HttpMethod.Post, ProfileRoute(profileId) + "/activate", null, cancellationToken);
    public Task SelectGroupAsync(string groupName, string proxy, CancellationToken cancellationToken = default) => SendNoContentAsync(HttpMethod.Put, ProxyApiRoutes.Proxy + "/groups/" + Uri.EscapeDataString(groupName) + "/selection", new SelectProxyGroupRequest(proxy), cancellationToken);
    public Task CloseConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => SendNoContentAsync(HttpMethod.Delete, ProxyApiRoutes.Proxy + "/connections/" + Uri.EscapeDataString(connectionId), null, cancellationToken);

    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, CancellationToken cancellationToken, bool idempotent = false)
    {
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.ServerUrl is null) throw new InvalidOperationException("RelaxKonOS session is not authenticated.");
        using var request = new HttpRequestMessage(method, new Uri(new Uri(session.ServerUrl), route.TrimStart('/')))
        {
            Content = body is null ? null : JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        if (idempotent) request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw await CreateRequestExceptionAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, cancellationToken) ?? throw new ProxyRequestException("proxy.response_empty");
    }

    private async Task SendNoContentAsync(HttpMethod method, string route, object? body, CancellationToken cancellationToken)
    {
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.ServerUrl is null) throw new InvalidOperationException("RelaxKonOS session is not authenticated.");
        using var request = new HttpRequestMessage(method, new Uri(new Uri(session.ServerUrl), route.TrimStart('/')))
        {
            Content = body is null ? null : JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw await CreateRequestExceptionAsync(response, cancellationToken);
    }

    private static string ProfileRoute(Guid profileId) => ProxyApiRoutes.Proxy + "/profiles/" + profileId;
    private static string SubscriptionRoute(Guid subscriptionId) => ProxyApiRoutes.Proxy + "/subscriptions/" + subscriptionId;

    private static async Task<ProxyRequestException> CreateRequestExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProxyProblemResponse>(RelaxKonOSJsonOptions.Default, cancellationToken);
            var problemCode = ExtractProblemCode(problem);
            if (problemCode is not null) return new ProxyRequestException(problemCode);
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or NotSupportedException) { }
        return new ProxyRequestException("proxy.http_" + (int)response.StatusCode);
    }

    private static string? ExtractProblemCode(ProxyProblemResponse? problem)
    {
        foreach (var candidate in new[] { problem?.ProblemCode, problem?.Detail, problem?.Title })
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.StartsWith("proxy.", StringComparison.Ordinal)) return candidate;

        const string prefix = "https://relaxkonos.app/problems/proxy.";
        return problem?.Type is { } type && type.StartsWith(prefix, StringComparison.Ordinal)
            ? type["https://relaxkonos.app/problems/".Length..]
            : null;
    }

    private sealed record ProxyProblemResponse(string? Detail, string? Title, string? Type, string? ProblemCode);
}
public sealed class ProxyRequestException(string problemCode) : Exception(problemCode) { public string ProblemCode { get; } = problemCode; }
