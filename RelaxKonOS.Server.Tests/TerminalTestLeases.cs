using RelaxKonOS.Server.Residency;

internal sealed class TerminalTestLeases : IResidentModuleWorkLeaseSource
{
    internal int Active;
    internal int RecoveryRequired;
    public ValueTask<IModuleWorkLease> AcquireAsync(string moduleId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref Active); return ValueTask.FromResult<IModuleWorkLease>(new Lease(this)); }
    private sealed class Lease(TerminalTestLeases owner) : IModuleWorkLease
    {
        private int _disposed, _recovery;
        public CancellationToken Stopping => CancellationToken.None;
        public void RequireRecovery() { if (Interlocked.Exchange(ref _recovery, 1) == 0) Interlocked.Increment(ref owner.RecoveryRequired); }
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner.Active); }
    }
}
