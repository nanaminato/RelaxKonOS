using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.WebServers;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ServerWebServersCapability(AppId appId, IAppPermissionManager permissions,
    ExternalServerConnection connection, IServerWebServers? client) : IServerWebServers
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
        RunAsync(() => Client.IntegrateCandidateAsync(candidateId, request, token), manage: true, token);

    public Task<WebServerOperationDto?> ApplyLifecycleAsync(string id, WebServerLifecycleAction action, CancellationToken token) =>
        RunAsync(() => Client.ApplyLifecycleAsync(id, action, token), manage: true, token);

    public Task<WebServerOperationDto?> ReloadAsync(string id, CancellationToken token) =>
        RunAsync(() => Client.ReloadAsync(id, token), manage: true, token);

    public Task<WebServerOperationDto?> GetOperationAsync(Guid operationId, CancellationToken token) =>
        RunAsync(() => Client.GetOperationAsync(operationId, token), manage: false, token);

    public Task<WebServerOperationDto?> CancelOperationAsync(Guid operationId, CancellationToken token) =>
        RunAsync(() => Client.CancelOperationAsync(operationId, token), manage: true, token);

    public Task<IReadOnlyList<WebServerSiteDto>?> ListSitesAsync(string id, CancellationToken token) =>
        RunAsync(() => Client.ListSitesAsync(id, token), manage: false, token);

    public Task<WebServerSiteDto?> UpsertSiteAsync(string id, UpsertWebServerSiteRequest request, CancellationToken token) =>
        RunAsync(() => Client.UpsertSiteAsync(id, request, token), manage: true, token);

    public async Task DeleteSiteAsync(string id, string siteId, DeleteWebServerSiteRequest request, CancellationToken token)
    {
        CheckAccess(manage: true, token);
        await Client.DeleteSiteAsync(id, siteId, request, token);
        CheckAccess(manage: true, token);
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
