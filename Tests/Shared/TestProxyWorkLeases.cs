using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Residency;

/// <summary>Explicit isolated-store test double; production always uses the residency coordinator.</summary>
internal sealed class TestProxyWorkLeases : IProxyWorkLeaseSource, IResidentModuleWorkLeaseSource
{
    internal int Active, Acquired, Recoveries;
    internal Exception? Failure { get; init; }
    public ValueTask<IModuleWorkLease> AcquireAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); if (Failure is not null) throw Failure;
        Interlocked.Increment(ref Active); Interlocked.Increment(ref Acquired);
        return ValueTask.FromResult<IModuleWorkLease>(new Lease(this));
    }
    ValueTask<IModuleWorkLease> IResidentModuleWorkLeaseSource.AcquireAsync(string moduleId, CancellationToken ct) => AcquireAsync(ct);
    private sealed class Lease(TestProxyWorkLeases owner) : IModuleWorkLease
    {
        private int released;
        public CancellationToken Stopping => CancellationToken.None;
        public void RequireRecovery() => Interlocked.Increment(ref owner.Recoveries);
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) Interlocked.Decrement(ref owner.Active); }
    }
}
