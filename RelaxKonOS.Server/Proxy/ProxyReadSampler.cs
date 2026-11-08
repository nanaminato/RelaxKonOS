using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Proxy;

/// <summary>Shares only an in-flight read. The sample owns execution and cleanup independently of each HTTP response.</summary>
internal sealed class ProxyReadSampler<T>(IServiceScopeFactory scopes, IResidentModuleWorkLeaseSource leases,
    Func<IServiceProvider, CancellationToken, Task<T>> readSample)
{
    private readonly object gate = new();
    private Sample? current;

    internal static void Register(IServiceCollection services, Func<IServiceProvider, CancellationToken, Task<T>> readSample) =>
        services.AddSingleton(provider => new ProxyReadSampler<T>(provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IResidentModuleWorkLeaseSource>(), readSample));

    public async Task<T> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Sample sample;
            Task<T>? read = null;
            lock (gate)
            {
                if (current is null)
                {
                    current = new Sample();
                    current.Read = RunAsync(current);
                }
                sample = current;
                if (!sample.Closing)
                {
                    sample.Waiters++;
                    read = sample.Read;
                }
            }

            if (read is not null)
            {
                using var registration = ct.Register(() => CancelWaiter(sample));
                return await read;
            }

            // A sample cancelled by its last reader must drain before another sample can start.
            await sample.Drained.Task.WaitAsync(ct);
        }
    }

    private void CancelWaiter(Sample sample)
    {
        lock (gate)
        {
            if (sample.Closing || --sample.Waiters != 0) return;
            sample.Closing = true;
            sample.Cancelling = sample.Cancellation.CancelAsync();
        }
    }

    private async Task<T> RunAsync(Sample sample)
    {
        // Defer controller resolution out of the admission lock.
        await Task.Yield();
        var cancellation = sample.Cancellation;
        IModuleWorkLease? lease = null;
        var cleanupFailed = false;
        try
        {
            lease = await leases.AcquireAsync(ServerModules.ProxyId, cancellation.Token);
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, lease.Stopping);
            stopping.Token.ThrowIfCancellationRequested();
            var scope = scopes.CreateAsyncScope();
            try
            {
                return await readSample(scope.ServiceProvider, stopping.Token);
            }
            finally
            {
                try { await scope.DisposeAsync(); }
                catch
                {
                    cleanupFailed = true;
                    lease.RequireRecovery();
                    throw;
                }
            }
        }
        finally
        {
            Task cancelling;
            lock (gate)
            {
                sample.Closing = true;
                cancelling = sample.Cancelling;
            }
            try { await cancelling; }
            finally
            {
                cancellation.Dispose();
                if (!cleanupFailed) lease?.Dispose();
                lock (gate) current = null;
                sample.Drained.SetResult();
            }
        }
    }

    private sealed class Sample
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<T> Read = null!;
        internal Task Cancelling = Task.CompletedTask;
        internal int Waiters;
        internal bool Closing;
    }
}
