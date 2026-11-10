using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Proxy;

/// <summary>Owns foreground requests and retains failed management scopes for the original generation.</summary>
internal sealed class ProxyRequestScopeOwner(IServiceScopeFactory scopes, IModuleWorkLeaseSource leases)
{
    private readonly List<IAsyncDisposable> failedScopes = [];
    internal int FailedScopeCount { get { lock (failedScopes) return failedScopes.Count; } }

    internal static async Task ExecuteAsync(HttpContext context, ModuleResidencyCoordinator coordinator,
        Func<IServiceProvider, CancellationToken, Task<IResult>> execute)
    {
        Task response;
        using (var admission = await coordinator.AcquireAsync(ServerModules.ProxyId, context.RequestAborted))
        {
            var owner = (ProxyRequestScopeOwner)admission.Runtime.Resolve(typeof(ProxyRequestScopeOwner));
            response = owner.ExecuteAsync(context, execute);
        }
        await response;
    }

    private Task ExecuteAsync(HttpContext context, Func<IServiceProvider, CancellationToken, Task<IResult>> execute) =>
        ResidentScopedResponse.ExecuteAsync(context, AcquireAsync, execute,
            scopeFactory: scopes, retainFailedScope: RetainFailedScope);

    private ValueTask<IModuleWorkLease> AcquireAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(leases.Acquire());
    }

    internal static void RetainFailedScope(IModuleWorkLease work, AsyncServiceScope scope)
    {
        if (work is ModuleWorkLeaseSource.WorkLease lease) RetainFailedResource(lease, scope);
    }

    internal static void RetainFailedResource(ModuleWorkLeaseSource.WorkLease lease, IAsyncDisposable resource)
        => ((ProxyRequestScopeOwner)lease.Runtime.Resolve(typeof(ProxyRequestScopeOwner))).RetainFailedResource(resource);

    private void RetainFailedScope(AsyncServiceScope scope) => RetainFailedResource(scope);

    private void RetainFailedResource(IAsyncDisposable resource)
    {
        lock (failedScopes) failedScopes.Add(resource);
    }
}
