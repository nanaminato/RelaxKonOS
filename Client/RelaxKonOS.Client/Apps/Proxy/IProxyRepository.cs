using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Client.Apps.Proxy;

public interface IProxyRepository
{
    Task<ProxyOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProxyProfileDto>> ListProfilesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProxySubscriptionDto>> ListSubscriptionsAsync(CancellationToken cancellationToken = default);
    Task<ProxySubscriptionDownloadOptionsDto> GetSubscriptionDownloadOptionsAsync(CancellationToken cancellationToken = default);
    Task<ProxySubscriptionDto> ImportSubscriptionAsync(ImportProxySubscriptionRequest request, CancellationToken cancellationToken = default);
    Task<ProxySubscriptionContentDto> GetSubscriptionContentAsync(Guid subscriptionId, CancellationToken cancellationToken = default);
    Task<ProxyOperationAcceptedDto> RefreshAllSubscriptionsAsync(CancellationToken cancellationToken = default);
    Task<ProxyOperationAcceptedDto> ActivateSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProxyGroupDto>> ListGroupsAsync(CancellationToken cancellationToken = default);
    Task<ProxyRoutingModeDto> GetRoutingModeAsync(CancellationToken cancellationToken = default);
    Task SetRoutingModeAsync(ProxyRoutingMode mode, CancellationToken cancellationToken = default);
    Task<ProxyDelayDto> TestProxyDelayAsync(string groupName, string proxyName, TestProxyDelayRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProxyConnectionDto>> ListConnectionsAsync(CancellationToken cancellationToken = default);
    Task<ProxyTrafficDto> GetTrafficAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProxyLogEntryDto>> ListLogsAsync(int limit = 200, CancellationToken cancellationToken = default);
    Task<ProxyDnsStatusDto> GetDnsStatusAsync(CancellationToken cancellationToken = default);
    Task<ProxySettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task UpdateSettingsAsync(UpdateProxySettingsRequest request, CancellationToken cancellationToken = default);
    Task<ProxyGeoDataDto> GetGeoDataAsync(CancellationToken cancellationToken = default);
    Task ConfigureGeoDataFromServerFileAsync(string filePath, CancellationToken cancellationToken = default);
    Task<ProxyRuntimeDto> GetRuntimeAsync(CancellationToken cancellationToken = default);
    Task<ProxyRuntimeDownloadDto?> GetManagedRuntimeDownloadAsync(string? version = null, CancellationToken cancellationToken = default);
    Task<ProxyOperationDto?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default);
    Task<ProxyOperationAcceptedDto> LifecycleAsync(ProxyLifecycleAction action, CancellationToken cancellationToken = default);
    Task<ProxyOperationAcceptedDto> EnableTunAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<ProxyOperationAcceptedDto> DisableTunAsync(CancellationToken cancellationToken = default);
    Task<ProxyOperationAcceptedDto> EmergencyDisableTunAsync(CancellationToken cancellationToken = default);
    Task<ProxyProfileDto> CreateProfileAsync(string name, string engineId, CancellationToken cancellationToken = default);
    Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<ProxyProfileDto> ActivateProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task SelectGroupAsync(string groupName, string proxy, CancellationToken cancellationToken = default);
    Task CloseConnectionAsync(string connectionId, CancellationToken cancellationToken = default);
}
