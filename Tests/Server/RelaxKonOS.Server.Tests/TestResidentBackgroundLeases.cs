using RelaxKonOS.Server.Residency;

internal sealed class TestResidentBackgroundLeases : IResidentModuleWorkLeaseSource
{
    internal int Active, Acquired, RecoveryRequired;
    public ValueTask<IModuleWorkLease> AcquireAsync(string moduleId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref Active); Interlocked.Increment(ref Acquired);
        return ValueTask.FromResult<IModuleWorkLease>(new Lease(this));
    }
    private sealed class Lease(TestResidentBackgroundLeases owner) : IModuleWorkLease
    {
        private int _disposed;
        public CancellationToken Stopping => CancellationToken.None;
        public void RequireRecovery() => Interlocked.Increment(ref owner.RecoveryRequired);
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner.Active); }
    }
}
