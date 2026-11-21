using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Proxy;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ServerProxyCapability(AppId appId, IAppPermissionManager permissions,
    ExternalServerConnection connection, Func<IServerProxy>? createClient, CancellationToken lifetime) : IServerProxy
{
    private readonly Lazy<IServerProxy> _client = new(() =>
        createClient?.Invoke() ?? throw new InvalidOperationException("Proxy is unavailable."));

    public Task<ProxyOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.GetOverviewAsync(token), null, cancellationToken);

    public Task<IReadOnlyList<ProxyProfileDto>> ListProfilesAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.ListProfilesAsync(token), null, cancellationToken);

    public Task<IReadOnlyList<ProxySubscriptionDto>> ListSubscriptionsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.ListSubscriptionsAsync(token), null, cancellationToken);

    public Task<ProxySubscriptionDownloadOptionsDto> GetSubscriptionDownloadOptionsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.GetSubscriptionDownloadOptionsAsync(token), CoreAppPermissions.ServerProxyManage, cancellationToken);

    public Task<ProxySubscriptionDto> ImportSubscriptionAsync(ImportProxySubscriptionRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.ImportSubscriptionAsync(request, token), CoreAppPermissions.ServerProxyManage, cancellationToken);

    public Task<ProxySubscriptionContentDto> GetSubscriptionContentAsync(Guid subscriptionId, CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.GetSubscriptionContentAsync(subscriptionId, token), CoreAppPermissions.ServerProxyManage, cancellationToken);

    public Task<ProxyOperationAcceptedDto> RefreshAllSubscriptionsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.RefreshAllSubscriptionsAsync(token), CoreAppPermissions.ServerProxyManage, cancellationToken);

    public Task<ProxyOperationAcceptedDto> ActivateSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.ActivateSubscriptionAsync(subscriptionId, token), CoreAppPermissions.ServerProxyManage, cancellationToken);

    public Task<IReadOnlyList<ProxyGroupDto>> ListGroupsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.ListGroupsAsync(token), null, cancellationToken);

    public Task<ProxyControllerConfigurationDto> GetControllerConfigurationAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.GetControllerConfigurationAsync(token), null, cancellationToken);

    public Task SetRoutingModeAsync(ProxyRoutingMode mode, CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.SetRoutingModeAsync(mode, token), CoreAppPermissions.ServerProxyManage, cancellationToken);

    public Task<ProxyDelayDto> TestProxyDelayAsync(string groupName, string proxyName, TestProxyDelayRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.TestProxyDelayAsync(groupName, proxyName, request, token), CoreAppPermissions.ServerProxyManage, cancellationToken);

    public Task<IReadOnlyList<ProxyConnectionDto>> ListConnectionsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.ListConnectionsAsync(token), null, cancellationToken);

    public Task<ProxyTrafficDto> GetTrafficAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.GetTrafficAsync(token), null, cancellationToken);

    public Task<IReadOnlyList<ProxyLogEntryDto>> ListLogsAsync(int limit = 200, CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.ListLogsAsync(limit, token), null, cancellationToken);

    public Task<ProxySettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.GetSettingsAsync(token), null, cancellationToken);

    public Task UpdateSettingsAsync(UpdateProxySettingsRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.UpdateSettingsAsync(request, token), CoreAppPermissions.ServerProxyManage, cancellationToken);

    public Task<RuntimeResourceSnapshot> GetResourcesAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.GetResourcesAsync(token), null, cancellationToken);

    public Task<RuntimeResourceReport> PrepareResourcesAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.PrepareResourcesAsync(token), CoreAppPermissions.ServerProxyManage, cancellationToken);

    public Task<ProxyRuntimeDto> GetRuntimeAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.GetRuntimeAsync(token), null, cancellationToken);

    public Task<ProxyOperationAcceptedDto> LifecycleAsync(ProxyLifecycleAction action, CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.LifecycleAsync(action, token), CoreAppPermissions.ServerProxyManage, cancellationToken);

    public Task<ProxyOperationAcceptedDto> EnableTunAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.EnableTunAsync(profileId, token), CoreAppPermissions.ServerProxyTunManage, cancellationToken);

    public Task<ProxyOperationAcceptedDto> DisableTunAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.DisableTunAsync(token), CoreAppPermissions.ServerProxyTunManage, cancellationToken);

    public Task<ProxyOperationAcceptedDto> EmergencyDisableTunAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.EmergencyDisableTunAsync(token), CoreAppPermissions.ServerProxyTunManage, cancellationToken);

    public Task<ProxyProfileDto> CreateProfileAsync(string name, string engineId, CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.CreateProfileAsync(name, engineId, token), CoreAppPermissions.ServerProxyManage, cancellationToken);

    public Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.DeleteProfileAsync(profileId, token), CoreAppPermissions.ServerProxyManage, cancellationToken);

    public Task<ProxyProfileDto> ActivateProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.ActivateProfileAsync(profileId, token), CoreAppPermissions.ServerProxyManage, cancellationToken);

    public Task SelectGroupAsync(string groupName, string proxy, CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.SelectGroupAsync(groupName, proxy, token), CoreAppPermissions.ServerProxyManage, cancellationToken);

    public Task CloseConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
        InvokeAsync((client, token) => client.CloseConnectionAsync(connectionId, token), CoreAppPermissions.ServerProxyManage, cancellationToken);

    public async Task<ProxyOperationDto?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        var operation = await InvokeAsync((client, token) => client.GetOperationAsync(operationId, token), null, cancellationToken);
        return operation is { } candidate && candidate.OperationId == operationId
            && (candidate.Kind.StartsWith("lifecycle.", StringComparison.Ordinal)
                || candidate.Kind.StartsWith("tun.", StringComparison.Ordinal)
                || candidate.Kind.StartsWith("subscription.", StringComparison.Ordinal))
            ? candidate : null;
    }

    private Task InvokeAsync(Func<IServerProxy, CancellationToken, Task> request, string? elevatedPermission,
        CancellationToken cancellationToken) =>
        InvokeAsync(async (client, token) => { await request(client, token); return true; }, elevatedPermission, cancellationToken);

    private async Task<T> InvokeAsync<T>(Func<IServerProxy, CancellationToken, Task<T>> request,
        string? elevatedPermission, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        var token = linked.Token;
        CheckAccess(elevatedPermission, token);
        using var observation = connection.ObserveInvalidation(linked.Cancel);
        var client = _client.Value;
        CheckAccess(elevatedPermission, token);
        var result = await request(client, token);
        CheckAccess(elevatedPermission, token);
        return result;
    }

    private void CheckAccess(string? elevatedPermission, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!connection.IsCurrent)
            throw new ProxyRequestException("proxy.session_changed");
        if (!permissions.IsGranted(appId, CoreAppPermissions.ServerProxyRead)
            || elevatedPermission is not null && (!connection.IsController || !permissions.IsGranted(appId, elevatedPermission)))
            throw new ProxyRequestException("proxy.permission_denied", 403);
    }
}
