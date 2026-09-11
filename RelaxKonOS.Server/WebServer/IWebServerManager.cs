using RelaxKonOS.Protocol.WebServers;

namespace RelaxKonOS.Server.WebServer;

public interface IWebServerManager
{
    Task<IReadOnlyList<WebServerDto>> DiscoverAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<WebServerDto>> ListAsync(CancellationToken cancellationToken);
    Task<WebServerStatusDto?> GetStatusAsync(string instanceId, CancellationToken cancellationToken);
    Task<WebServerConfigTestResultDto?> TestConfigurationAsync(string instanceId, CancellationToken cancellationToken);
    Task<WebServerOperationDto?> IntegrateAsync(string instanceId, string idempotencyKey, IntegrateWebServerRequest request, string? actor, CancellationToken cancellationToken);
    Task<WebServerOperationDto?> ApplyLifecycleAsync(string instanceId, WebServerLifecycleAction action, string idempotencyKey, string? actor, CancellationToken cancellationToken);
    Task<WebServerOperationDto?> ReloadAsync(string instanceId, string idempotencyKey, string? actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<WebServerSiteDto>?> ListSitesAsync(string instanceId, CancellationToken cancellationToken);
    Task<WebServerSiteDto?> UpsertSiteAsync(string instanceId, UpsertWebServerSiteRequest request, CancellationToken cancellationToken);
    Task<bool?> DeleteSiteAsync(string instanceId, string siteId, CancellationToken cancellationToken);
}

/// <summary>
/// A provider implements the operations for one concrete web-server product.  Providers never
/// expose HTTP endpoints themselves: <see cref="IWebServerManager"/> owns discovery aggregation
/// and instance-to-provider routing.
/// </summary>
public interface IWebServerProvider
{
    string ProviderId { get; }

    Task<IReadOnlyList<WebServerDto>> DiscoverAsync(CancellationToken cancellationToken);
    Task<WebServerStatusDto?> GetStatusAsync(string instanceId, CancellationToken cancellationToken);
    Task<WebServerConfigTestResultDto?> TestConfigurationAsync(string instanceId, CancellationToken cancellationToken);
    Task<WebServerOperationDto?> IntegrateAsync(string instanceId, string idempotencyKey, IntegrateWebServerRequest request, string? actor, CancellationToken cancellationToken);
    Task<WebServerOperationDto?> ApplyLifecycleAsync(string instanceId, WebServerLifecycleAction action, string idempotencyKey, string? actor, CancellationToken cancellationToken);
    Task<WebServerOperationDto?> ReloadAsync(string instanceId, string idempotencyKey, string? actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<WebServerSiteDto>?> ListSitesAsync(string instanceId, CancellationToken cancellationToken);
    Task<WebServerSiteDto?> UpsertSiteAsync(string instanceId, UpsertWebServerSiteRequest request, CancellationToken cancellationToken);
    Task<bool?> DeleteSiteAsync(string instanceId, string siteId, CancellationToken cancellationToken);
}
