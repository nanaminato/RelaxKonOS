using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Proxy;

/// <summary>Shares only an in-flight read. The sample owns execution and cleanup independently of each HTTP response.</summary>
internal sealed class ProxyReadSampler<T>(IServiceScopeFactory scopes, IModuleWorkLeaseSource leases,
    Func<IServiceProvider, CancellationToken, Task<T>> readSample)
{
    private readonly object gate = new();
    private Sample? current;
    private AsyncServiceScope? failedScope;

    internal static void Register(IServiceCollection services,
        Func<IServiceProvider, IServerModuleRuntime, CancellationToken, Task<T>> readSample) =>
        services.AddSingleton(new ProxyReadSamplerRegistration(typeof(ProxyReadSampler<T>), (scopes, leases, runtime) =>
            new ProxyReadSampler<T>(scopes, leases, (services, ct) => readSample(services, runtime, ct))));

    internal static async Task<T> ReadAsync(IServiceProvider services, CancellationToken ct)
    {
        Task<T> read;
        var coordinator = services.GetRequiredService<ModuleResidencyCoordinator>();
        using (var admission = await coordinator.AcquireAsync(ServerModules.ProxyId, ct))
            read = Resolve(admission.Runtime).ReadAsync(ct);
        return await read;
    }

    internal static ProxyReadSampler<T> Resolve(IServerModuleRuntime runtime) =>
        (ProxyReadSampler<T>)runtime.Resolve(typeof(ProxyReadSampler<T>));

    public async Task<T> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Sample sample;
            Task<T>? read = null;
            lock (gate)
            {
                if (failedScope.HasValue)
                    throw new ModuleUnavailableException("module-recovery-blocked", 30, "Proxy read scope cleanup requires recovery.");
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
            cancellation.Token.ThrowIfCancellationRequested();
            lease = leases.Acquire();
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
                    // Disposal can stop partway through the scope. Keep the original owner and resources.
                    lock (gate) failedScope = scope;
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

internal sealed record ProxyReadSamplerRegistration(Type ServiceType,
    Func<IServiceScopeFactory, IModuleWorkLeaseSource, IServerModuleRuntime, object> Create);
