using RelaxKonOS.Protocol.WebServers;

namespace RelaxKonOS.Client.Apps.WebServers;

/// <summary>
/// Client-side facade for the host-global web server API. Discovery and read are stateless;
/// integrate/reload start long-running operations tracked by an operation id.
/// </summary>
public interface IRemoteWebServerClient
{
    Task<IReadOnlyList<WebServerDto>> DiscoverAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WebServerDto>> ListAsync(CancellationToken cancellationToken = default);
    Task<WebServerStatusDto?> GetStatusAsync(string id, CancellationToken cancellationToken = default);
    Task<WebServerConfigTestResultDto?> TestConfigurationAsync(string id, CancellationToken cancellationToken = default);
    Task<WebServerOperationDto?> IntegrateAsync(string id, IntegrateWebServerRequest request, CancellationToken cancellationToken = default);
    Task<WebServerOperationDto?> ApplyLifecycleAsync(string id, WebServerLifecycleAction action, CancellationToken cancellationToken = default);
    Task<WebServerOperationDto?> ReloadAsync(string id, CancellationToken cancellationToken = default);
    Task<WebServerOperationDto?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default);
    Task<WebServerOperationDto?> CancelOperationAsync(Guid operationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WebServerSiteDto>?> ListSitesAsync(string id, CancellationToken cancellationToken = default);
    Task<WebServerSiteDto?> UpsertSiteAsync(string id, UpsertWebServerSiteRequest request, CancellationToken cancellationToken = default);
    Task DeleteSiteAsync(string id, string siteId, CancellationToken cancellationToken = default);
}
