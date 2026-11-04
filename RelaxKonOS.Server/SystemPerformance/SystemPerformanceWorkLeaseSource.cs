using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.SystemPerformance;

public interface ISystemPerformanceWorkLeaseSource
{
    ValueTask<IModuleWorkLease> AcquireAsync(CancellationToken ct);
}

internal sealed class SystemPerformanceWorkLeaseSource(ModuleResidencyCoordinator coordinator) : ISystemPerformanceWorkLeaseSource
{
    public async ValueTask<IModuleWorkLease> AcquireAsync(CancellationToken ct)
    {
        using var admission = await coordinator.AcquireAsync(ServerModules.SystemPerformanceId, ct);
        return new ModuleWorkLeaseSource(coordinator, admission.Runtime).Acquire();
    }
}

/// <summary>Resident metadata/work ownership only, never a second sampler or management container.</summary>
internal sealed class SystemPerformanceWorkRuntimeFactory : IServerModuleRuntimeFactory
{
    public string ModuleId => ServerModules.SystemPerformanceId;
    public ValueTask<IServerModuleRuntime> CreateAsync(IServiceProvider services, ServerModuleContext context, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult<IServerModuleRuntime>(new Runtime()); }
    private sealed class Runtime : IServerModuleRuntime
    {
        public string ModuleId => ServerModules.SystemPerformanceId;
        public object Resolve(Type serviceType) => throw new InvalidOperationException("Resident performance work ownership has no service resolver.");
        public ValueTask<ModuleUnloadBlocker?> CanUnloadAsync(CancellationToken ct) => ValueTask.FromResult<ModuleUnloadBlocker?>(new("module-resident-work-owner"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
