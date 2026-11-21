using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.ProxyServer;

using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ServerProxyServerCapability(AppId appId, IAppPermissionManager permissions,
    ExternalServerConnection connection, Func<IServerProxyServer>? createClient, CancellationToken lifetime) : IServerProxyServer
{
    private readonly Lazy<IServerProxyServer> _client = new(() =>
        createClient?.Invoke() ?? throw new InvalidOperationException("Proxy Server is unavailable."));

    public Task<ProxyServerSnapshot> ReadAsync(CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.ReadAsync(token), null, cancellationToken);

    public Task<ProxyServerStatus> StatusAsync(CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.StatusAsync(token), null, cancellationToken);

    public Task<IReadOnlyList<ProxyServerRuntimeRelease>> ReleasesAsync(CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.ReleasesAsync(token), null, cancellationToken);

    public Task<ProxyServerValidation> ValidateAsync(CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.ValidateAsync(token), CoreAppPermissions.ServerProxyServerManage, cancellationToken);

    public Task<IReadOnlyList<ProxyServerExportFormatOption>> ExportFormatsAsync(Guid credentialId, CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.ExportFormatsAsync(credentialId, token), CoreAppPermissions.ServerProxyServerExport, cancellationToken);

    public Task<ProxyServerExport> ExportAsync(Guid credentialId, string format, CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.ExportAsync(credentialId, format, token), CoreAppPermissions.ServerProxyServerExport, cancellationToken);

    public Task<ProxyServerSubscriptionIssued> IssueSubscriptionAsync(IssueProxyServerSubscription request, CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.IssueSubscriptionAsync(request, token), CoreAppPermissions.ServerProxyServerExport, cancellationToken);

    public Task<ProxyOperationAcceptedDto> SaveConfigurationAsync(SaveProxyServerConfiguration request, string key, CancellationToken cancellationToken) =>
        ManageAsync((client, token) => client.SaveConfigurationAsync(request, key, token), cancellationToken);

    public Task<ProxyOperationAcceptedDto> SavePrincipalAsync(SaveProxyServerPrincipal request, string key, CancellationToken cancellationToken) =>
        ManageAsync((client, token) => client.SavePrincipalAsync(request, key, token), cancellationToken);

    public Task<ProxyOperationAcceptedDto> DeletePrincipalAsync(Guid id, string key, CancellationToken cancellationToken) =>
        ManageAsync((client, token) => client.DeletePrincipalAsync(id, key, token), cancellationToken);

    public Task<ProxyOperationAcceptedDto> SaveCredentialAsync(ProxyServerCredentialRequest request, string key, CancellationToken cancellationToken) =>
        ManageAsync((client, token) => client.SaveCredentialAsync(request, key, token), cancellationToken);

    public Task<ProxyOperationAcceptedDto> RotateCredentialAsync(Guid id, ProxyServerCredentialRotation request, string key, CancellationToken cancellationToken) =>
        ManageAsync((client, token) => client.RotateCredentialAsync(id, request, key, token), cancellationToken);

    public Task<ProxyOperationAcceptedDto> DeleteCredentialAsync(Guid id, string key, CancellationToken cancellationToken) =>
        ManageAsync((client, token) => client.DeleteCredentialAsync(id, key, token), cancellationToken);

    public Task<ProxyOperationAcceptedDto> ApplyAsync(string key, CancellationToken cancellationToken) =>
        ManageAsync((client, token) => client.ApplyAsync(key, token), cancellationToken);

    public Task<ProxyOperationAcceptedDto> RollbackAsync(string key, CancellationToken cancellationToken) =>
        ManageAsync((client, token) => client.RollbackAsync(key, token), cancellationToken);

    public Task<ProxyOperationAcceptedDto> RunLifecycleAsync(ProxyServerAction action, string key, CancellationToken cancellationToken) =>
        ManageAsync((client, token) => client.RunLifecycleAsync(action, key, token), cancellationToken);

    public Task<ProxyOperationAcceptedDto> RevokeSubscriptionAsync(Guid id, string key, CancellationToken cancellationToken) =>
        ManageAsync((client, token) => client.RevokeSubscriptionAsync(id, key, token), cancellationToken);

    public async Task<ProxyOperationDto> OperationAsync(Guid id, CancellationToken cancellationToken)
    {
        var operation = await InvokeAsync((client, token) => client.OperationAsync(id, token), null, cancellationToken);
        if (operation.OperationId != id || !operation.Kind.StartsWith("proxy_server.", StringComparison.Ordinal))
            throw new InvalidDataException("The server returned an invalid Proxy Server operation.");
        return operation;
    }

    private Task<ProxyOperationAcceptedDto> ManageAsync(
        Func<IServerProxyServer, CancellationToken, Task<ProxyOperationAcceptedDto>> request, CancellationToken cancellationToken) =>
        InvokeAsync(request, CoreAppPermissions.ServerProxyServerManage, cancellationToken);

    private async Task<T> InvokeAsync<T>(Func<IServerProxyServer, CancellationToken, Task<T>> request,
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
            throw new InvalidOperationException("The application's Proxy Server connection has changed.");
        if (!permissions.IsGranted(appId, CoreAppPermissions.ServerProxyServerRead)
            || elevatedPermission is not null && (!permissions.IsGranted(appId, elevatedPermission)
                || !connection.IsController))
            throw new UnauthorizedAccessException();
    }
}
