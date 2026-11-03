namespace RelaxKonOS.Server.Residency;

/// <summary>A running module takes protection before publishing work that outlives its caller.</summary>
internal sealed class ModuleWorkLeaseSource(ModuleResidencyCoordinator coordinator, IServerModuleRuntime runtime) : IModuleWorkLeaseSource
{
    public IModuleWorkLease Acquire()
    {
        var lease = coordinator.AcquireForRuntime(runtime);
        return new WorkLease(lease, lease.Stopping);
    }
    private sealed class WorkLease(ModuleLease lease, CancellationToken stopping) : IModuleWorkLease
    {
        public CancellationToken Stopping => stopping;
        public void RequireRecovery() => lease.Quarantine(this, "module-background-work-failed");
        public void Dispose() => lease.Dispose();
    }
}
