using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

internal static class ProxyResidencyChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine($"PASS PROXY RESIDENCY {++count}: {text}"); }
        await using (var f = new Fixture())
        {
            var before = f.Coordinator.Describe(ServerModules.ProxyId)!;
            Check(before.State == ModuleResidencyState.Resident && !before.Releasable && before.ActiveLeases == 0
                && !f.Modules.KnownReleasableModuleIds.Contains(ServerModules.ProxyId), "Work registration keeps Proxy resident without loading management or advertising sleep");
            Check(await f.Coordinator.TrySleepAsync(ServerModules.ProxyId, default) == "module-resident-work-owner", "Even an unused work owner cannot report successful Proxy sleep");
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var request = new CancellationTokenSource();
            var item = await f.Store.EnqueueAsync("resident-one", "test", (_, token) =>
                ProxyOperationScope.RunAsync(f.Services.GetRequiredService<IServiceScopeFactory>(), "operator", "test",
                    async (services, execution) => { services.GetRequiredService<Cleanup>().Configure(entered, release); await Task.Yield(); return null; }, token), request.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); request.Cancel();
            Check(f.Coordinator.HasBackgroundWork(ServerModules.ProxyId) && f.Coordinator.Describe(ServerModules.ProxyId)!.ActiveLeases == 1
                && (await f.Store.GetAsync(item.OperationId, default))!.State == ProxyOperationState.Running,
                "Accepted work owns a real coordinator lease through async scope disposal after HTTP cancellation");
            var replay = await f.Store.EnqueueAsync("resident-one", "test", (_, _) => throw new Exception("Replay executed"), default);
            Check(replay.OperationId == item.OperationId && f.Coordinator.Describe(ServerModules.ProxyId)!.ActiveLeases == 1, "Idempotent replay does not acquire a second work claim");
            var stop = f.Store.StopAsync(default); var coordinatorStop = f.Coordinator.DisposeAsync().AsTask();
            Check(!stop.IsCompleted && !coordinatorStop.IsCompleted, "Both ledger and residency shutdown wait for actual async cleanup");
            try { await f.Store.EnqueueAsync("late", "test", (_, _) => Task.FromResult<string?>(null), default); throw new Exception("Late accepted"); }
            catch (ProxyOperationValidationException) { Check(true, "Shutdown closes new durable admission"); }
            release.TrySetResult(); await Task.WhenAll(stop, coordinatorStop).WaitAsync(TimeSpan.FromSeconds(5));
            Check(!f.Coordinator.HasBackgroundWork(ServerModules.ProxyId) && (await f.Store.GetAsync(item.OperationId, default))!.State == ProxyOperationState.Succeeded,
                "Scope cleanup and durable terminal confirmation precede work-lease release");
        }
        await using (var f = new Fixture())
        {
            var item = await f.Store.EnqueueAsync("cleanup-failure", "test", (_, token) =>
                ProxyOperationScope.RunAsync(f.Services.GetRequiredService<IServiceScopeFactory>(), "operator", "test",
                    (services, execution) => { services.GetRequiredService<Cleanup>().Fail = true; return Task.FromResult<string?>(null); }, token), default);
            try { await f.Store.StopAsync(default); throw new Exception("Failed cleanup was ignored"); } catch (ProxyOperationCleanupException) { }
            Check(f.Coordinator.Describe(ServerModules.ProxyId) is { State: ModuleResidencyState.Faulted, BlockedBy: "module-recovery-blocked", ActiveLeases: 0 }
                && await f.Coordinator.TrySleepAsync(ServerModules.ProxyId, default) == "module-recovery-blocked", "Failed scope disposal quarantines the real resident generation after retaining recovery ownership");
            Check((await f.Store.InspectResponsibilitiesAsync(default)).Count > 0 && (await f.Store.GetAsync(item.OperationId, default))!.ProblemCode == ProxyProblemCodes.OperationInterrupted,
                "Quarantine does not discard failed scope or durable interruption responsibility");
        }
        await using (var f = new Fixture())
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var item = await f.Store.EnqueueAsync("terminal-failure", "test", async (_, _) => { entered.TrySetResult(); await release.Task; return null; }, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.CreateDirectory(Path.Combine(f.Root, "proxy-operations.json.new")); release.TrySetResult();
            try { await f.Store.StopAsync(default); throw new Exception("Terminal failure ignored"); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            Check(f.Coordinator.Describe(ServerModules.ProxyId) is { State: ModuleResidencyState.Faulted, BlockedBy: "module-recovery-blocked" }
                && (await f.Store.InspectResponsibilitiesAsync(default)).Count > 0, "Terminal persistence failure quarantines the same work owner without removing durable responsibility");
        }
        await using (var f = new Fixture())
        {
            var obstruction = Path.Combine(f.Root, "proxy-operations.json.new"); Directory.CreateDirectory(obstruction);
            try { await f.Store.EnqueueAsync("first-write-failed", "test", (_, _) => throw new Exception("Unaccepted work executed"), default); throw new Exception("Failed queued write accepted"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            Check(!f.Coordinator.HasBackgroundWork(ServerModules.ProxyId) && f.Coordinator.Describe(ServerModules.ProxyId)!.ActiveLeases == 0
                && !File.Exists(Path.Combine(f.Root, "proxy-operations.json")), "First queued write failure returns its work lease without a fake durable receipt");
            Directory.Delete(obstruction);
            var item = await f.Store.EnqueueAsync("first-write-failed", "test", (_, _) => Task.FromResult<string?>(null), default);
            await f.Store.StopAsync(default);
            Check((await f.Store.GetAsync(item.OperationId, default))!.State == ProxyOperationState.Succeeded && !f.Coordinator.HasBackgroundWork(ServerModules.ProxyId),
                "Retry after a refused first write can acquire a fresh lease and persist exactly one operation");
        }
        var rejectedRoot = Path.Combine(Path.GetTempPath(), "proxy-lease-refused-" + Guid.NewGuid().ToString("N"));
        var refused = new TestProxyWorkLeases { Failure = new ProxyOperationValidationException(ProxyProblemCodes.OperationInterrupted) };
        var rejectedStore = new ProxyOperationStore(new Paths(rejectedRoot), Microsoft.Extensions.Logging.Abstractions.NullLogger<ProxyOperationStore>.Instance, refused);
        try { await rejectedStore.EnqueueAsync("refused", "test", (_, _) => throw new Exception("Refused executed"), default); throw new Exception("Refused accepted"); }
        catch (ProxyOperationValidationException) { Check(!Directory.Exists(rejectedRoot) && refused.Active == 0, "Lease admission failure precedes durable queued acceptance and never starts work"); }
        await CheckParserAsync(Check);
        await CheckCollectionBudgetAsync(Check);
        Console.WriteLine($"Proxy residency and independent assembly checks passed: {count}");
    }

    private static async Task CheckCollectionBudgetAsync(Action<bool, string> check)
    {
        var gate = new ProxySubscriptionExecutionGate();
        var pending = FillCollectionBudget(gate, check);
        for (var i = 0; pending.Any(code => code.IsAlive) && i < 30; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(10);
        }
        check(pending.All(code => !code.IsAlive), "Collection budget holds only weak references even after concurrent admission rejection");
        var generations = new List<WeakReference>();
        for (var generation = 0; generation < 100; generation++)
        {
            var output = gate.Normalize("vless://test-id@example.com:443#cycle", out var code);
            if (!output.Contains("example.com")) throw new Exception("Conversion changed after reopening budget");
            generations.Add(code);
            for (var i = 0; code.IsAlive && i < 30; i++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(10);
            }
            if (code.IsAlive) throw new Exception("Conversion context survived bounded collection observation");
        }
        check(generations.All(code => !code.IsAlive), "100 real conversion generations collect and reuse admission after budget saturation without retaining old code");
        var failure = CaptureConversionFailure(gate);
        check(failure.Error.InnerException is null && failure.Error.Message == "Proxy subscription conversion failed.",
            "Private conversion failure crosses the Host boundary only as a neutral exception without an inner implementation error");
        for (var i = 0; failure.Code.IsAlive && i < 30; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(10);
        }
        check(!failure.Code.IsAlive, "Retaining the completed Host failure and its stack does not retain failed conversion code");
        GC.KeepAlive(failure.Error);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (WeakReference Code, InvalidDataException Error) CaptureConversionFailure(ProxySubscriptionExecutionGate gate)
    {
        WeakReference code = null!;
        try { gate.Normalize(null!, out code); }
        catch (InvalidDataException error) { return (code, error); }
        throw new Exception("Invalid private conversion unexpectedly succeeded");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference[] FillCollectionBudget(ProxySubscriptionExecutionGate gate, Action<bool, string> check)
    {
        var retained = new List<object>();
        var pending = new List<WeakReference>();
        for (var i = 0; i < ProxySubscriptionExecutionGate.MaxOutstandingContexts; i++)
        {
            gate.Normalize("vless://test-id@example.com:443#retained", out var code);
            retained.Add(code.Target ?? throw new Exception("Context collected before test retention"));
            pending.Add(code);
        }
        var rejected = 0;
        Parallel.For(0, 32, _ =>
        {
            try { gate.Normalize("vless://test-id@example.com:443#overflow", out var ignored); }
            catch (InvalidDataException error) when (error.Message.Contains("collection is pending") && error.InnerException is null)
            { Interlocked.Increment(ref rejected); }
        });
        check(rejected == 32, "Retained conversion contexts cap concurrent admission with neutral Host errors and no forced collection");
        GC.KeepAlive(retained);
        return pending.ToArray();
    }

    private static async Task CheckParserAsync(Action<bool,string> check)
    {
        var hostReferences = typeof(ProxyOperationStore).Assembly.GetReferencedAssemblies();
        check(!hostReferences.Any(name => name.Name == "RelaxKonOS.ServerModule.Proxy"), "Server has no static reference to the private Proxy implementation assembly");
        var input = "vless://test-id@example.com:443?security=tls#example";
        var output = ProxySubscriptionContentNormalizer.Normalize(input, out var code);
        check(output.Contains("proxies:") && output.Contains("example.com") && output.Contains("vless"), "Production gate executes the moved URI conversion and returns only shared text");
        for (var i = 0; code.IsAlive && i < 30; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(10); }
        check(!code.IsAlive, "Completed conversion releases its collectible private code while the resident Host remains alive");
        foreach (var pair in new[] {
            ("vmess://" + System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"add\":\"example.com\",\"port\":443,\"id\":\"test-id\"}")), "vmess"),
            ("trojan://secret@example.com:443#trojan", "trojan"),
            ("ss://YWVzLTI1Ni1nY206cGFzc3dvcmQ@example.com:443#ss", "ss"),
            ("hy2://secret@example.com:443#hy2", "hysteria2"),
            ("tuic://test-id:secret@example.com:443#tuic", "tuic") })
            check(ProxySubscriptionContentNormalizer.Normalize(pair.Item1).Contains("type: " + pair.Item2), "Independent converter preserves " + pair.Item2 + " subscription support");
        var invalid = "vmess://invalid";
        check(ProxySubscriptionContentNormalizer.Normalize(invalid) == invalid, "Invalid subscription conversion retains original input for the existing validation stage");
        var yaml = "proxies: []\nrules: []\n";
        check(ProxySubscriptionContentNormalizer.Normalize(yaml) == yaml, "Native YAML and existing parsing behavior remain unchanged through the independent boundary");
    }
    private sealed class Cleanup : IAsyncDisposable
    {
        private TaskCompletionSource? entered, release;
        internal bool Fail;
        internal void Configure(TaskCompletionSource began, TaskCompletionSource wait) { entered = began; release = wait; }
        public async ValueTask DisposeAsync() { entered?.TrySetResult(); if (release is not null) await release.Task; if (Fail) throw new IOException("isolated cleanup failure"); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "proxy-residency-" + Guid.NewGuid().ToString("N"));
        internal string Root => root;
        private readonly WebApplication app;
        internal IServiceProvider Services => app.Services;
        internal ServerModules Modules { get; }
        internal ModuleResidencyCoordinator Coordinator => Services.GetRequiredService<ModuleResidencyCoordinator>();
        internal ProxyOperationStore Store => Services.GetRequiredService<ProxyOperationStore>();
        internal Fixture()
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            Modules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.ProxyId });
            Modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
            builder.Services.AddSingleton<IProxyPlatformPaths>(new Paths(root)); builder.Services.AddScoped<Cleanup>();
            app = builder.Build();
        }
        public async ValueTask DisposeAsync() { await app.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class Paths(string root) : IProxyPlatformPaths
    {
        public string GetStateDirectory() => root;
        public string GetProtectedConfigurationDirectory() => Path.Combine(root, "protected");
        public string GetEngineVersionsDirectory(string engineId) => Path.Combine(root, engineId, "versions");
        public string GetEngineDataDirectory(string engineId) => Path.Combine(root, engineId, "data");
        public string GetSanitizedLogDirectory() => Path.Combine(root, "logs");
    }
}
