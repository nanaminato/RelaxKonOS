using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Proxy;

/// <summary>Owns foreground management requests and any scope whose cleanup could not be confirmed.</summary>
internal sealed class ProxyRequestScopeOwner(IServiceScopeFactory scopes, IModuleWorkLeaseSource leases)
{
    private readonly List<AsyncServiceScope> failedScopes = [];

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
        if (work is ModuleWorkLeaseSource.WorkLease lease
            && lease.Runtime.Resolve(typeof(ProxyRequestScopeOwner)) is ProxyRequestScopeOwner owner)
            owner.RetainFailedScope(scope);
    }

    private void RetainFailedScope(AsyncServiceScope scope)
    {
        lock (failedScopes) failedScopes.Add(scope);
    }
}
