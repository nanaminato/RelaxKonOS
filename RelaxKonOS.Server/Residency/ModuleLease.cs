namespace RelaxKonOS.Server.Residency;

/// <summary>
/// A claim on a module runtime. The lease is what makes "release when idle" safe: the coordinator can
/// not dispose a runtime that still has a lease, and a request that holds one is allowed to touch the
/// module's services. Dispose it after the protected work has finished, including a streamed response.
/// </summary>
internal sealed class ModuleLease : IDisposable
{
    private ModuleResidencyCoordinator? _coordinator;

    internal ModuleLease(ModuleResidencyCoordinator coordinator, IServerModuleRuntime runtime)
    {
        _coordinator = coordinator;
        Runtime = runtime;
    }

    internal IServerModuleRuntime Runtime { get; }

    public void Dispose()
    {
        var coordinator = Interlocked.Exchange(ref _coordinator, null);
        coordinator?.Release(Runtime.ModuleId);
    }
}
