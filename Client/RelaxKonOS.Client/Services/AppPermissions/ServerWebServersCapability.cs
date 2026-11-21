using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.WebServers;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ServerWebServersCapability(AppId appId, IAppPermissionManager permissions,
    ExternalServerConnection connection, IServerWebServers? client, IHostElevationBroker? elevation) : IServerWebServers
{
    private IServerWebServers Client => client ?? throw new InvalidOperationException("The web server client is unavailable.");

    public Task<CurrentNginxDto> SelectCurrentNginxAsync(CurrentNginxDto request, CancellationToken token) =>
        throw new UnauthorizedAccessException("Current Nginx selection belongs to Component Center.");

    public Task<CurrentNginxDto> GetCurrentNginxAsync(CancellationToken token) =>
        RunAsync(() => Client.GetCurrentNginxAsync(token), manage: false, token);

    public Task<IReadOnlyList<WebServerDto>> DiscoverAsync(CancellationToken token) =>
        RunAsync(() => Client.DiscoverAsync(token), manage: false, token);

    public Task<IReadOnlyList<WebServerDto>> ListAsync(CancellationToken token) =>
        RunAsync(() => Client.ListAsync(token), manage: false, token);

    public Task<IReadOnlyList<WebServerIntegrationCandidateDto>> ListIntegrationCandidatesAsync(CancellationToken token) =>
        RunAsync(() => Client.ListIntegrationCandidatesAsync(token), manage: false, token);

    public Task<WebServerStatusDto?> GetStatusAsync(string id, CancellationToken token) =>
        RunAsync(() => Client.GetStatusAsync(id, token), manage: false, token);

    public Task<WebServerConfigTestResultDto?> TestConfigurationAsync(string id, CancellationToken token) =>
        RunAsync(() => Client.TestConfigurationAsync(id, token), manage: false, token);

    public Task<WebServerInstallCatalogDto?> GetManagedInstallCatalogAsync(CancellationToken token) =>
        RunAsync(() => Client.GetManagedInstallCatalogAsync(token), manage: false, token);

    public Task<WebServerInstallDownloadDto?> GetManagedInstallDownloadAsync(string version, CancellationToken token) =>
        RunAsync(() => Client.GetManagedInstallDownloadAsync(version, token), manage: false, token);

    public Task<WebServerOperationDto?> IntegrateCandidateAsync(string candidateId, IntegrateWebServerRequest request, CancellationToken token) =>
        RunElevatedAsync(HostElevationCapability.NginxConfigurationWrite, candidateId,
            () => Client.IntegrateCandidateAsync(candidateId, request, token), token);

    public Task<WebServerOperationDto?> ApplyLifecycleAsync(string id, WebServerLifecycleAction action, CancellationToken token) =>
        RunElevatedAsync(action == WebServerLifecycleAction.EnableAcmeHttp01
                ? HostElevationCapability.NginxConfigurationWrite : HostElevationCapability.NginxLifecycle,
            id, () => Client.ApplyLifecycleAsync(id, action, token), token);

    public Task<WebServerOperationDto?> ReloadAsync(string id, CancellationToken token) =>
        RunElevatedAsync(HostElevationCapability.NginxLifecycle, id, () => Client.ReloadAsync(id, token), token);

    public Task<WebServerOperationDto?> GetOperationAsync(Guid operationId, CancellationToken token) =>
        RunAsync(() => Client.GetOperationAsync(operationId, token), manage: false, token);

    public Task<WebServerOperationDto?> CancelOperationAsync(Guid operationId, CancellationToken token) =>
        RunAsync(() => Client.CancelOperationAsync(operationId, token), manage: true, token);

    public Task<IReadOnlyList<WebServerSiteDto>?> ListSitesAsync(string id, CancellationToken token) =>
        RunAsync(() => Client.ListSitesAsync(id, token), manage: false, token);

    public Task<WebServerSiteDto?> UpsertSiteAsync(string id, UpsertWebServerSiteRequest request, CancellationToken token) =>
        RunElevatedAsync(HostElevationCapability.NginxConfigurationWrite, id, () => Client.UpsertSiteAsync(id, request, token), token);

    public Task DeleteSiteAsync(string id, string siteId, DeleteWebServerSiteRequest request, CancellationToken token) =>
        RunElevatedAsync(HostElevationCapability.NginxConfigurationWrite, id, async () =>
        {
            await Client.DeleteSiteAsync(id, siteId, request, token);
            return true;
        }, token);

    private Task<T> RunElevatedAsync<T>(HostElevationCapability capability, string target, Func<Task<T>> operation,
        CancellationToken token)
    {
        CheckAccess(manage: true, token);
        var broker = elevation ?? throw new InvalidOperationException("Host authorization is unavailable.");
        return broker.ExecuteAsync(capability, target, () => RunAsync(operation, manage: true, token), token);
    }

    private async Task<T> RunAsync<T>(Func<Task<T>> operation, bool manage, CancellationToken token)
    {
        CheckAccess(manage, token);
        var result = await operation();
        CheckAccess(manage, token);
        return result;
    }

    private void CheckAccess(bool manage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!permissions.IsGranted(appId, CoreAppPermissions.ServerWebServersRead)
            || manage && !permissions.IsGranted(appId, CoreAppPermissions.ServerWebServersManage))
            throw new UnauthorizedAccessException();
        if (!connection.IsCurrent)
            throw new InvalidOperationException("The application's web server context has changed.");
    }
}
