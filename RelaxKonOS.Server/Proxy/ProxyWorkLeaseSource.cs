using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Proxy;

public interface IProxyWorkLeaseSource
{
    ValueTask<IModuleWorkLease> AcquireAsync(CancellationToken ct);
}

internal sealed class ProxyWorkLeaseSource(ModuleResidencyCoordinator coordinator) : IProxyWorkLeaseSource
{
    public async ValueTask<IModuleWorkLease> AcquireAsync(CancellationToken ct)
    {
        try
        {
            using var admission = await coordinator.AcquireAsync(ServerModules.ProxyId, ct);
            return new ModuleWorkLeaseSource(coordinator, admission.Runtime).Acquire();
        }
        catch (ModuleUnavailableException) { throw new ProxyOperationValidationException(RelaxKonOS.Protocol.Proxy.ProxyProblemCodes.OperationInterrupted); }
    }
}

/// <summary>A resident work owner, not a second management container or a releasable Proxy module.</summary>
internal sealed class ProxyWorkRuntimeFactory : IServerModuleRuntimeFactory
{
    public string ModuleId => ServerModules.ProxyId;
    public ValueTask<IServerModuleRuntime> CreateAsync(IServiceProvider services, ServerModuleContext context, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult<IServerModuleRuntime>(new Runtime()); }
    private sealed class Runtime : IServerModuleRuntime
    {
        public string ModuleId => ServerModules.ProxyId;
        public object Resolve(Type serviceType) => throw new InvalidOperationException("Resident Proxy work ownership has no management service resolver.");
        public ValueTask<ModuleUnloadBlocker?> CanUnloadAsync(CancellationToken ct) => ValueTask.FromResult<ModuleUnloadBlocker?>(new("module-resident-work-owner"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
