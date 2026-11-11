using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.SystemPerformance;

internal sealed class TestSystemPerformanceWorkLeases : ISystemPerformanceWorkLeaseSource
{
    internal int Active;
    internal int Acquired;
    public ValueTask<IModuleWorkLease> AcquireAsync(CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref Active); Interlocked.Increment(ref Acquired); return ValueTask.FromResult<IModuleWorkLease>(new Lease(this)); }
    private sealed class Lease(TestSystemPerformanceWorkLeases owner) : IModuleWorkLease
    {
        private int _disposed;
        public CancellationToken Stopping => CancellationToken.None;
        public void RequireRecovery() => throw new InvalidOperationException("Unexpected recovery request");
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner.Active); }
    }
}
