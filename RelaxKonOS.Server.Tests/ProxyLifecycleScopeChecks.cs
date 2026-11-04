internal static class ProxyLifecycleScopeChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine($"PASS PROXY SCOPE {++count}: {label}"); }
        for (var index = 0; index < 6; index++)
        {
            var root = Path.Combine(Path.GetTempPath(), "proxy-lifecycle-scope-" + Guid.NewGuid().ToString("N"));
            var paths = new TestProxyPaths(root); var state = new State();
            var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<IProxyPlatformPaths>(paths);
            services.AddSingleton<IProxyWorkLeaseSource, TestProxyWorkLeases>(); services.AddSingleton<ProxyOperationStore>(); services.AddSingleton<ProxyAuditStore>(); services.AddSingleton<ProxyLifecycleOperations>();
            services.AddScoped<IProxyLifecycleService>(_ => Create<IProxyLifecycleService>(state));
            services.AddScoped<IProxyTunSafetyService>(_ => Create<IProxyTunSafetyService>(state));
            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            var operations = provider.GetRequiredService<ProxyOperationStore>();
            try
            {
                var queue = provider.GetRequiredService<ProxyLifecycleOperations>(); var id = Guid.NewGuid();
                var address = IPAddress.Parse("192.0.2.17");
                using var request = new CancellationTokenSource();
                ProxyOperationDto accepted;
                await using (var requestScope = provider.CreateAsyncScope())
                {
                    Check(state.Created == 0, "Root queues do not construct lifecycle or TUN execution graphs");
                    accepted = index < 3
                        ? await queue.EnqueueLifecycleAsync("once", (ProxyLifecycleAction)index, "actor", request.Token)
                        : await queue.EnqueueTunAsync("once", (ProxyTunOperation)(index - 3), id, address, "actor", request.Token);
                }
                request.Cancel(); await state.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(state.Created == 1 && !state.Token.CanBeCanceled && state.Disposed == 0,
                    "Accepted lifecycle/TUN work outlives its HTTP scope and cancellation");
                if (index == 3) Check(state.Profile == id && state.Address!.Equals(address) && !ReferenceEquals(state.Address, address),
                    "TUN activation owns a value copy of the Server-observed management address");
                state.Release.TrySetResult(); await state.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var stop = operations.StopAsync(default);
                Check(!stop.IsCompleted && (await operations.GetAsync(accepted.OperationId, default))!.State == ProxyOperationState.Running,
                    "Lifecycle/TUN terminal success and host drain wait for scope cleanup");
                state.ReleaseDisposal.TrySetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(5));
                Check(state.Disposed == 1 && (await operations.GetAsync(accepted.OperationId, default))!.State == ProxyOperationState.Succeeded,
                    "Independent lifecycle/TUN scope finishes audit, disposal and terminal persistence");
                for (var retry = 0; retry < 10 && state.Instance!.IsAlive; retry++)
                { await Task.Delay(20); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
                Check(!state.Instance!.IsAlive, "Completed lifecycle/TUN facades are not retained by the root queue or ledger");
            }
            finally
            {
                state.Release.TrySetResult(); state.ReleaseDisposal.TrySetResult();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
        Console.WriteLine($"Proxy lifecycle/TUN scope checks passed: {count}");
    }
    private static T Create<T>(State state) where T : class
    {
        var instance = DispatchProxy.Create<T, Execution>(); var stub = (Execution)(object)instance;
        stub.State = state; state.Created++; state.Instance = new(instance); return instance;
    }
    private sealed class State
    {
        public int Created, Disposed;
        public WeakReference? Instance;
        public Guid Profile;
        public IPAddress? Address;
        public CancellationToken Token;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously),
            Disposing = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseDisposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public class Execution : DispatchProxy, IAsyncDisposable
    {
        private State state = null!;
        internal object State { set => state = (State)value; }
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            state.Token = (CancellationToken)arguments![^1]!;
            if (method!.Name == "EnableAsync") { state.Profile = (Guid)arguments[0]!; state.Address = (IPAddress?)arguments[1]; }
            return RunAsync();
        }
        private async Task<string?> RunAsync() { state.Started.TrySetResult(); await state.Release.Task; return null; }
        public async ValueTask DisposeAsync()
        { state.Disposing.TrySetResult(); await state.ReleaseDisposal.Task; state.Disposed++; }
    }
}
