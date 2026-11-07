using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Proxy.Platform;

namespace RelaxKonOS.Server.Proxy;

/// <summary>Resolves a controller only for a TUN observation or transition.</summary>
internal sealed class ScopedProxyTunRuntimeController(IServiceScopeFactory scopes) : IProxyTunRuntimeController
{
    public async Task<string?> SetEnabledAsync(ProxyManagementRouteSnapshot snapshot, bool enabled, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MihomoTunRuntimeController>()
            .SetEnabledAsync(snapshot, enabled, cancellationToken);
    }

    public async Task<ProxyTunRuntimeObservation> IsEnabledAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MihomoTunRuntimeController>().IsEnabledAsync(cancellationToken);
    }
}
