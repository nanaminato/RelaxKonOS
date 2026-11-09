using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Proxy;

/// <summary>Owns foreground configuration work and any scope whose cleanup could not be confirmed.</summary>
internal sealed class ProxyWriteScopeOwner(IServiceScopeFactory scopes, IModuleWorkLeaseSource leases)
{
    private readonly List<AsyncServiceScope> failedScopes = [];

    internal static async Task ExecuteAsync(HttpContext context, ModuleResidencyCoordinator coordinator,
        Func<IServiceProvider, CancellationToken, Task<IResult>> execute)
    {
        Task response;
        using (var admission = await coordinator.AcquireAsync(ServerModules.ProxyId, context.RequestAborted))
        {
            var owner = (ProxyWriteScopeOwner)admission.Runtime.Resolve(typeof(ProxyWriteScopeOwner));
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

    private void RetainFailedScope(AsyncServiceScope scope)
    {
        lock (failedScopes) failedScopes.Add(scope);
    }
}
