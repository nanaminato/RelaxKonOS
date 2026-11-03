namespace RelaxKonOS.Server.Residency;

/// <summary>
/// A claim on a module runtime. The lease is what makes "release when idle" safe: the coordinator can
/// not dispose a runtime that still has a lease, and a request that holds one is allowed to touch the
/// module's services. Dispose it after the protected work has finished, including a streamed response.
/// </summary>
internal sealed class ModuleLease : IDisposable
{
    private ModuleResidencyCoordinator? _coordinator;
    private readonly bool _backgroundWork;
    private IServerModuleRuntime? _runtime;

    internal ModuleLease(ModuleResidencyCoordinator coordinator, IServerModuleRuntime runtime, bool backgroundWork = false)
    {
        _coordinator = coordinator;
        _runtime = runtime;
        Stopping = coordinator.StoppingToken;
        _backgroundWork = backgroundWork;
    }

    internal IServerModuleRuntime Runtime => _runtime ?? throw new ObjectDisposedException(nameof(ModuleLease));
    internal CancellationToken Stopping { get; }
    internal void Quarantine(object resource, string problemCode = "module-stream-cleanup-failed")
    {
        var coordinator = _coordinator;
        var runtime = _runtime;
        if (coordinator is not null && runtime is not null) coordinator.Quarantine(runtime, resource, problemCode);
    }

    public void Dispose()
    {
        var coordinator = Interlocked.Exchange(ref _coordinator, null);
        if (coordinator is null) return;
        var runtime = Interlocked.Exchange(ref _runtime, null)!;
        coordinator.Release(runtime.ModuleId, _backgroundWork);
    }
}
