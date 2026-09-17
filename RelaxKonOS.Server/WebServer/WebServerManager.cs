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

    public async Task<IReadOnlyList<WebServerIntegrationCandidateDto>> ListIntegrationCandidatesAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<WebServerIntegrationCandidateDto>();
        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates.AddRange(await provider.ListIntegrationCandidatesAsync(cancellationToken));
        }
        return candidates;
    }

    public async Task<WebServerStatusDto?> GetStatusAsync(string instanceId, CancellationToken cancellationToken)
        => await WithProviderAsync(instanceId, (provider, ct) => provider.GetStatusAsync(instanceId, ct), cancellationToken);

    public async Task<WebServerConfigTestResultDto?> TestConfigurationAsync(string instanceId, CancellationToken cancellationToken)
        => await WithProviderAsync(instanceId, (provider, ct) => provider.TestConfigurationAsync(instanceId, ct), cancellationToken);

    public async Task<WebServerOperationDto?> IntegrateCandidateAsync(string candidateId, string idempotencyKey, IntegrateWebServerRequest request, string? actor, CancellationToken cancellationToken)
    {
        foreach (var provider in _providers)
        {
            var candidates = await provider.ListIntegrationCandidatesAsync(cancellationToken);
            if (candidates.Any(candidate => string.Equals(candidate.Id, candidateId, StringComparison.Ordinal)))
                return await provider.IntegrateCandidateAsync(candidateId, idempotencyKey, request, actor, cancellationToken);
        }
        return null;
    }

    public async Task<WebServerOperationDto?> ApplyLifecycleAsync(string instanceId, WebServerLifecycleAction action, string idempotencyKey, string? actor, CancellationToken cancellationToken)
        => await WithProviderAsync(instanceId, (provider, ct) => provider.ApplyLifecycleAsync(instanceId, action, idempotencyKey, actor, ct), cancellationToken);

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
