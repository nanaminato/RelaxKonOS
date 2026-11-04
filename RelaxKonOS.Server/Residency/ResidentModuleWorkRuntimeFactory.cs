using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Residency;

/// <summary>Metadata and work identity for a resident owner; never creates a second management service graph.</summary>
internal sealed class ResidentModuleWorkRuntimeFactory(string moduleId) : IServerModuleRuntimeFactory
{
    public string ModuleId => moduleId;
    public ValueTask<IServerModuleRuntime> CreateAsync(IServiceProvider services, ServerModuleContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IServerModuleRuntime>(new Runtime(moduleId));
    }
    private sealed class Runtime(string moduleId) : IServerModuleRuntime
    {
        public string ModuleId => moduleId;
        public object Resolve(Type serviceType) => throw new InvalidOperationException("Resident work metadata does not resolve management services.");
        public ValueTask<ModuleUnloadBlocker?> CanUnloadAsync(CancellationToken ct) =>
            ValueTask.FromResult<ModuleUnloadBlocker?>(new("module-resident-work-owner"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Framework work admission for explicitly registered resident modules.</summary>
public interface IResidentModuleWorkLeaseSource
{
    ValueTask<IModuleWorkLease> AcquireAsync(string moduleId, CancellationToken ct = default);
}

internal sealed class ResidentModuleWorkLeaseSource(ModuleResidencyCoordinator coordinator) : IResidentModuleWorkLeaseSource
{
    public async ValueTask<IModuleWorkLease> AcquireAsync(string moduleId, CancellationToken ct = default)
    {
        using var admission = await coordinator.AcquireAsync(moduleId, ct);
        return new ModuleWorkLeaseSource(coordinator, admission.Runtime).Acquire();
    }
}
