using RelaxKonOS.Protocol.WebServers;

namespace RelaxKonOS.Server.WebServer;

/// <summary>
/// Host-global facade over concrete web-server providers.
///
/// This is the extensibility boundary used by HTTP endpoints and other RelaxKonOS modules. Nginx,
/// IIS, Apache, and future products are providers beneath it; adding one does not require callers
/// to take a dependency on a product-specific manager.
/// </summary>
internal sealed class WebServerManager(IEnumerable<IWebServerProvider> providers) : IWebServerManager
{
    private readonly IReadOnlyList<IWebServerProvider> _providers = providers
        .OrderBy(provider => provider.ProviderId, StringComparer.Ordinal)
        .ToArray();

    public async Task<IReadOnlyList<WebServerDto>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var discovered = new List<WebServerDto>();
        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            discovered.AddRange(await provider.DiscoverAsync(cancellationToken));
        }
        return discovered;
    }

    public Task<IReadOnlyList<WebServerDto>> ListAsync(CancellationToken cancellationToken)
        => DiscoverAsync(cancellationToken);

    public async Task<WebServerStatusDto?> GetStatusAsync(string instanceId, CancellationToken cancellationToken)
        => await WithProviderAsync(instanceId, (provider, ct) => provider.GetStatusAsync(instanceId, ct), cancellationToken);

    public async Task<WebServerConfigTestResultDto?> TestConfigurationAsync(string instanceId, CancellationToken cancellationToken)
        => await WithProviderAsync(instanceId, (provider, ct) => provider.TestConfigurationAsync(instanceId, ct), cancellationToken);

    public async Task<WebServerOperationDto?> InstallManagedAsync(string providerId, string idempotencyKey, InstallManagedWebServerRequest request, string? actor, CancellationToken cancellationToken)
    {
        var provider = _providers.FirstOrDefault(candidate => string.Equals(candidate.ProviderId, providerId, StringComparison.Ordinal));
        return provider is null ? null : await provider.InstallManagedAsync(idempotencyKey, request, actor, cancellationToken);
    }

    public async Task<WebServerInstallPackageDto?> UploadManagedPackageAsync(string providerId, string fileName, Stream content, CancellationToken cancellationToken)
    {
        var provider = _providers.FirstOrDefault(candidate => string.Equals(candidate.ProviderId, providerId, StringComparison.Ordinal));
        return provider is null ? null : await provider.UploadManagedPackageAsync(fileName, content, cancellationToken);
    }

    public async Task<WebServerInstallCatalogDto?> GetManagedInstallCatalogAsync(string providerId, CancellationToken cancellationToken)
    {
        var provider = _providers.FirstOrDefault(candidate => string.Equals(candidate.ProviderId, providerId, StringComparison.Ordinal));
        return provider is null ? null : await provider.GetManagedInstallCatalogAsync(cancellationToken);
    }

    public async Task<WebServerInstallDownloadDto?> GetManagedInstallDownloadAsync(string providerId, string? version, CancellationToken cancellationToken)
    {
        var provider = _providers.FirstOrDefault(candidate => string.Equals(candidate.ProviderId, providerId, StringComparison.Ordinal));
        return provider is null ? null : await provider.GetManagedInstallDownloadAsync(version, cancellationToken);
    }

    public async Task<WebServerOperationDto?> IntegrateAsync(string instanceId, string idempotencyKey, IntegrateWebServerRequest request, string? actor, CancellationToken cancellationToken)
        => await WithProviderAsync(instanceId, (provider, ct) => provider.IntegrateAsync(instanceId, idempotencyKey, request, actor, ct), cancellationToken);

    public async Task<WebServerOperationDto?> ApplyLifecycleAsync(string instanceId, WebServerLifecycleAction action, string idempotencyKey, string? actor, CancellationToken cancellationToken)
        => await WithProviderAsync(instanceId, (provider, ct) => provider.ApplyLifecycleAsync(instanceId, action, idempotencyKey, actor, ct), cancellationToken);

    public async Task<WebServerOperationDto?> UninstallManagedAsync(string instanceId, string idempotencyKey, UninstallManagedWebServerRequest request, string? actor, CancellationToken cancellationToken)
        => await WithProviderAsync(instanceId, (provider, ct) => provider.UninstallManagedAsync(instanceId, idempotencyKey, request, actor, ct), cancellationToken);

    public async Task<WebServerOperationDto?> ReloadAsync(string instanceId, string idempotencyKey, string? actor, CancellationToken cancellationToken)
        => await WithProviderAsync(instanceId, (provider, ct) => provider.ReloadAsync(instanceId, idempotencyKey, actor, ct), cancellationToken);

    public async Task<IReadOnlyList<WebServerSiteDto>?> ListSitesAsync(string instanceId, CancellationToken cancellationToken)
        => await WithProviderAsync(instanceId, (provider, ct) => provider.ListSitesAsync(instanceId, ct), cancellationToken);

    public async Task<WebServerSiteDto?> UpsertSiteAsync(string instanceId, UpsertWebServerSiteRequest request, CancellationToken cancellationToken)
        => await WithProviderAsync(instanceId, (provider, ct) => provider.UpsertSiteAsync(instanceId, request, ct), cancellationToken);

    public async Task<bool?> DeleteSiteAsync(string instanceId, string siteId, CancellationToken cancellationToken)
    {
        foreach (var provider in _providers)
        {
            var instances = await provider.DiscoverAsync(cancellationToken);
            if (instances.Any(instance => string.Equals(instance.Id, instanceId, StringComparison.Ordinal)))
                return await provider.DeleteSiteAsync(instanceId, siteId, cancellationToken);
        }
        return null;
    }

    private async Task<T?> WithProviderAsync<T>(string instanceId, Func<IWebServerProvider, CancellationToken, Task<T?>> operation, CancellationToken cancellationToken)
        where T : class
    {
        foreach (var provider in _providers)
        {
            var instances = await provider.DiscoverAsync(cancellationToken);
            if (!instances.Any(instance => string.Equals(instance.Id, instanceId, StringComparison.Ordinal))) continue;
            return await operation(provider, cancellationToken);
        }
        return null;
    }
}
