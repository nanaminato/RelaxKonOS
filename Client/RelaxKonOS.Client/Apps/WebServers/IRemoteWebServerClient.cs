using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Client.Apps.WebServers;

/// <summary>
/// Client-side facade for the host-global web server API. Discovery and read are stateless;
/// integrate/reload start long-running operations tracked by an operation id.
/// </summary>
public interface IRemoteWebServerClient
{
    Task<IReadOnlyList<WebServerDto>> DiscoverAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WebServerDto>> ListAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WebServerIntegrationCandidateDto>> ListIntegrationCandidatesAsync(CancellationToken cancellationToken = default);
    Task<WebServerStatusDto?> GetStatusAsync(string id, CancellationToken cancellationToken = default);
    Task<WebServerConfigTestResultDto?> TestConfigurationAsync(string id, CancellationToken cancellationToken = default);
    Task<WebServerInstallCatalogDto?> GetManagedInstallCatalogAsync(CancellationToken cancellationToken = default);
    Task<WebServerInstallDownloadDto?> GetManagedInstallDownloadAsync(string version, CancellationToken cancellationToken = default);
    Task<InstallationFileReferenceDto?> UploadManagedPackageAsync(string fileName, Stream content, CancellationToken cancellationToken = default);
    Task<bool> ElevateConfigurationAsync(string candidateId, string password, CancellationToken cancellationToken = default);
    Task<WebServerOperationDto?> IntegrateCandidateAsync(string candidateId, IntegrateWebServerRequest request, CancellationToken cancellationToken = default);
    Task<WebServerOperationDto?> ApplyLifecycleAsync(string id, WebServerLifecycleAction action, CancellationToken cancellationToken = default);
    Task<WebServerOperationDto?> ReloadAsync(string id, CancellationToken cancellationToken = default);
    Task<WebServerOperationDto?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default);
    Task<WebServerOperationDto?> CancelOperationAsync(Guid operationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WebServerSiteDto>?> ListSitesAsync(string id, CancellationToken cancellationToken = default);
    Task<WebServerSiteDto?> UpsertSiteAsync(string id, UpsertWebServerSiteRequest request, CancellationToken cancellationToken = default);
    Task DeleteSiteAsync(string id, string siteId, CancellationToken cancellationToken = default);
}
