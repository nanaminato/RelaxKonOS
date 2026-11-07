using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.Server.ProxyServer;

internal static class ExpiryBoundaryChecks
{
    public static async Task RunAsync(string root)
    {
        var store = new ProxyServerStore(new Paths(Path.Combine(root, "expiry-boundary")), new EphemeralDataProtectionProvider());
        // A pending publication requires reconciliation on every pass until its durable outcome is known.
        await store.WriteAsync(ProxyServerState.Empty with { PendingPublication = new(new(0, []), [], [], []) }, default);
        var state = new State();
        var services = new ServiceCollection();
        services.AddScoped<IProxyServerExpiryExecutor>(_ => new Executor(state));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var worker = new ProxyServerExpiryService(provider.GetRequiredService<IServiceScopeFactory>(), store,
            NullLogger<ProxyServerExpiryService>.Instance);
        Require(state.Created == 0, "Creating the scheduler must not construct management services.");
        await worker.StartAsync(default);
        await state.FirstDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(state.Created == 1 && state.Disposed == 1, "A failed pass must release its scope before waiting.");
        // Use the production interval: an exception must not stop the scheduler or retain the
        // failed executor, and the next pass must receive an independent instance.
        await state.SecondDisposed.Task.WaitAsync(TimeSpan.FromSeconds(40));
        Require(state.Created == 2 && state.Disposed == 2, "A successful retry must also release its scope.");
        for (var attempt = 0; attempt < 10 && state.Instances.Any(reference => reference.IsAlive); attempt++)
        {
            await Task.Delay(20); // Leave the executor's inline async continuation stack.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        Require(state.Instances.All(reference => !reference.IsAlive), "The waiting timer must not retain completed executor instances.");
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));

        var cancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationServices = new ServiceCollection();
        cancellationServices.AddScoped<IProxyServerExpiryExecutor>(_ => new WaitingExecutor(cancellation, disposing, releaseDisposal, disposed));
        await using var cancellationProvider = cancellationServices.BuildServiceProvider();
        using var waitingWorker = new ProxyServerExpiryService(cancellationProvider.GetRequiredService<IServiceScopeFactory>(), store,
            NullLogger<ProxyServerExpiryService>.Instance);
        await waitingWorker.StartAsync(default);
        await cancellation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = waitingWorker.StopAsync(new CancellationToken(canceled: true));
        try
        {
            await disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!stop.IsCompleted && !disposed.Task.IsCompleted,
                "Stopping must wait while the executor's asynchronous disposal is blocked.");
        }
        finally { releaseDisposal.TrySetResult(); }
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Require(disposed.Task.IsCompletedSuccessfully, "Stopping an active pass must cancel it and await async scope disposal.");
        Console.WriteLine("Proxy expiry boundary: lazy construction, failed-pass release, fresh retry, successful release, weak-reference collection and cancellation disposal passed.");
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class State
    {
        public int Created;
        public int Disposed;
        public List<WeakReference> Instances = [];
        public TaskCompletionSource FirstDisposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondDisposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Executor(State state) : IProxyServerExpiryExecutor, IAsyncDisposable
    {
        private readonly int number = Interlocked.Increment(ref state.Created);
        public Task ReconcileExpiryAsync(CancellationToken ct)
        {
            state.Instances.Add(new WeakReference(this));
            return number == 1 ? Task.FromException(new ProxyServerException("proxy_server.recovery_required")) : Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref state.Disposed);
            (number == 1 ? state.FirstDisposed : state.SecondDisposed).TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class WaitingExecutor(TaskCompletionSource started, TaskCompletionSource disposing,
        TaskCompletionSource releaseDisposal, TaskCompletionSource disposed)
        : IProxyServerExpiryExecutor, IAsyncDisposable
    {
        public async Task ReconcileExpiryAsync(CancellationToken ct)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        public async ValueTask DisposeAsync()
        {
            disposing.TrySetResult();
            await releaseDisposal.Task;
            disposed.TrySetResult();
        }
    }
}
