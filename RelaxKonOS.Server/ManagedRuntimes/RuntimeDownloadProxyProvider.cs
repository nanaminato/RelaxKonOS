using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.Docker;

namespace RelaxKonOS.Server.ManagedRuntimes;

/// <summary>
/// Resolves the one download route a managed-runtime operation may use, from the saved host
/// download-proxy preference. The value stays a Server-side decision: no client field and no process
/// environment variable can introduce a route, and a configured but unusable preference is refused
/// rather than silently replaced with a direct connection that the operator did not ask for.
/// </summary>
public interface IRuntimeDownloadProxyProvider
{
    /// <summary><c>null</c> means a direct connection is the configured behaviour.</summary>
    Task<RuntimeDownloadProxy?> ResolveAsync(CancellationToken ct);
}

/// <summary>
/// The resolver is supplied as a factory on purpose. Reading the preference needs the Docker proxy
/// resolver, which needs the proxy settings service, which needs the Mihomo publication coordinator,
/// which owns this runtime service: resolving that chain while the graph is being built is a cycle.
/// Deferring to the first operation both breaks the cycle and keeps the graph honest, because a route is
/// only ever read while an operation is actually being accepted.
/// </summary>
public sealed class RuntimeDownloadProxyProvider(Func<IDockerProxyResolver> resolver) : IRuntimeDownloadProxyProvider
{
    public async Task<RuntimeDownloadProxy?> ResolveAsync(CancellationToken ct)
    {
        var resolution = await resolver().ResolveAsync(ct);
        if (!resolution.Enabled || !resolution.ApplyToRuntimeDownloads) return null;
        if (!resolution.IsUsable) throw new RuntimeLifecycleException("runtime.download_proxy_unavailable");
        try { return RuntimeDownloadPolicy.Create(resolution.HttpsProxy, resolution.NoProxy); }
        // A saved value the Helper would reject is reported here, before an operation is accepted.
        catch (RuntimePolicyException ex) { throw new RuntimeLifecycleException(ex.ProblemCode); }
    }
}
