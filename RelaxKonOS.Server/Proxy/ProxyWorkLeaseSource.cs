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

/// <summary>The resident Proxy work owner also owns the in-flight read samplers.</summary>
internal sealed class ProxyWorkRuntimeFactory : IServerModuleRuntimeFactory
{
    public string ModuleId => ServerModules.ProxyId;
    public ValueTask<IServerModuleRuntime> CreateAsync(IServiceProvider services, ServerModuleContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IServerModuleRuntime>(new Runtime(services));
    }

    private sealed class Runtime : IServerModuleRuntime
    {
        private readonly Dictionary<Type, object> samplers;

        internal Runtime(IServiceProvider services)
        {
            var scopes = services.GetRequiredService<IServiceScopeFactory>();
            var leases = new ModuleWorkLeaseSource(services.GetRequiredService<ModuleResidencyCoordinator>(), this);
            samplers = services.GetServices<ProxyReadSamplerRegistration>()
                .ToDictionary(registration => registration.ServiceType, registration => registration.Create(scopes, leases, this));
        }

        public string ModuleId => ServerModules.ProxyId;
        public object Resolve(Type serviceType) => samplers.TryGetValue(serviceType, out var sampler)
            ? sampler : throw new InvalidOperationException($"Proxy runtime does not own {serviceType}.");
        public ValueTask<ModuleUnloadBlocker?> CanUnloadAsync(CancellationToken ct) => ValueTask.FromResult<ModuleUnloadBlocker?>(new("module-resident-work-owner"));
        public ValueTask DisposeAsync()
        {
            samplers.Clear();
            return ValueTask.CompletedTask;
        }
    }
}
