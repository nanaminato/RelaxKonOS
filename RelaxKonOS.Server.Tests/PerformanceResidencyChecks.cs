using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.SystemMonitor;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.SystemPerformance;

internal static class PerformanceResidencyChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string text)
        { if (!ok) throw new Exception(text); Console.WriteLine($"PASS PERFORMANCE RESIDENCY {++count}: {text}"); }
        var registry = new PerformanceSubscriptionRegistry();
        var transitions = new List<bool>(); registry.CollectionNeededChanged += transitions.Add;
        registry.Subscribe("first"); registry.Subscribe("second"); registry.Unsubscribe("first");
        Check(registry.NeedsCollection, "Second connection remains registered while another connection already needs collection");
        var revision = registry.CollectionState.Revision;
        registry.Subscribe("second"); registry.Unsubscribe("absent");
        Check(registry.CollectionState.Revision == revision && transitions.SequenceEqual([true]), "Duplicate membership and missing unsubscribe do not create collection transitions");
        registry.Unsubscribe("second");
        Check(!registry.NeedsCollection && transitions.SequenceEqual([true, false]), "Only the last connection ends the collection period");
        var demand = registry.RequestCollection(TimeSpan.FromSeconds(10));
        registry.Subscribe("during-demand"); demand.Dispose();
        Check(registry.NeedsCollection, "A connection established during REST demand survives demand disposal");
        registry.Unsubscribe("during-demand");
        var before = registry.CollectionState;
        try { registry.RequestCollection(TimeSpan.FromMilliseconds(-2)); throw new Exception("Invalid demand accepted"); } catch (ArgumentOutOfRangeException) { }
        Check(registry.CollectionState == before && !registry.NeedsCollection, "Invalid timer windows are rejected before incrementing demand state");
        var failedRegistry = new PerformanceSubscriptionRegistry();
        failedRegistry.CollectionNeededChanged += needed => { if (needed) throw new IOException("test observer"); };
        try { failedRegistry.RequestCollection(TimeSpan.FromSeconds(10)); throw new Exception("Observer failure ignored"); } catch (IOException) { }
        Check(!failedRegistry.NeedsCollection, "Failed demand admission rolls back its count without leaving permanent sampling demand");
        Parallel.For(0, 32, i => registry.Subscribe("parallel-" + i));
        Parallel.For(0, 31, i => registry.Unsubscribe("parallel-" + i));
        Check(registry.NeedsCollection, "Concurrent registration retains all active members instead of only the first");
        registry.Unsubscribe("parallel-31");

        await using (var f = new Fixture())
        {
            await f.Sampler.StartAsync(default); await Task.Delay(100);
            Check(f.Source.Reads == 0 && !f.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId), "Idle sampler performs no source reads and acquires no work lease");
            f.Subscriptions.Subscribe("old");
            var old = await f.Source.NextAsync();
            Check(f.Coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases == 1, "An actual source read owns a real coordinator work lease");
            f.Subscriptions.Unsubscribe("old"); f.Subscriptions.Subscribe("new");
            old.Result.TrySetResult(Sample(100));
            var baseline = await f.Source.NextAsync();
            Check(f.Sampler.GetLatest() is null && f.History.GetRecent(60).Count == 0, "Late old-period source result cannot populate the new period's baseline or history");
            baseline.Result.TrySetResult(Sample(1));
            var next = await f.Source.NextAsync();
            Check(f.Sampler.GetLatest() is null, "The first source result in the new period establishes a baseline rather than publishing a cross-period delta");
            var published = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Sampler.SnapshotAvailable += _ => published.TrySetResult(f.Coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases);
            next.Result.TrySetResult(Sample(2));
            var leasesDuringPublication = await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Sampler.GetLatest() is { Sequence: 1, Cpu.TotalPercent: 80 } && leasesDuringPublication == 1,
                "Valid adjacent-period counters publish once while synchronous event dispatch is still protected");
            var inFlight = await f.Source.NextAsync();
            using var budget = new CancellationTokenSource();
            var ownerStop = f.Sampler.StopAsync(budget.Token);
            var coordinatorStop = f.Coordinator.DisposeAsync().AsTask();
            Check(inFlight.Token.IsCancellationRequested && !coordinatorStop.IsCompleted && f.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId),
                "Stopping signals the read but preserves its lease while a non-cooperative source remains unfinished");
            budget.Cancel();
            try { await ownerStop; } catch (OperationCanceledException) { }
            Check(f.Sampler.ExecuteTask is { IsCompleted: false } && f.Coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases == 1
                && f.History.Latest() is not null, "Stop wait budget cannot clear a live read's protection or claim final state cleanup");
            inFlight.Result.TrySetResult(Sample(3));
            await f.Sampler.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            await coordinatorStop.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.History.Latest() is null && f.Sampler.GetLatest() is null && !f.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId),
                "Actual loop completion precedes final baseline/history cleanup and zero coordinator work");
        }
        await using (var f = new Fixture())
        {
            await f.Coordinator.DisposeAsync(); f.Subscriptions.Subscribe("unavailable"); await f.Sampler.StartAsync(default);
            await UntilAsync(() => f.Sampler.ExecuteTask?.IsCompleted == true);
            Check(f.Source.Reads == 0 && f.History.Latest() is null, "Closed coordinator refuses OS collection and the sampler exits without unprotected fallback");
        }
        await using (var f = new Fixture())
        {
            f.Subscriptions.Subscribe("failure"); await f.Sampler.StartAsync(default);
            var first = await f.Source.NextAsync(); first.Result.TrySetException(new IOException("test native read"));
            await UntilAsync(() => !f.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId));
            var retry = await f.Source.NextAsync();
            Check(f.Coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases == 1 && f.History.Latest() is null,
                "Read failure returns its lease; the next authorized sample obtains fresh protection and no false snapshot");
            f.Subscriptions.Unsubscribe("failure"); retry.Result.TrySetResult(Sample(1));
            await UntilAsync(() => !f.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId));
            Check(f.History.Latest() is null, "A read finishing after demand disappears cannot restore idle history");
        }
        await using (var f = new Fixture())
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await f.Sampler.GetInfoAsync(cancelled.Token); throw new Exception("Cancelled info accepted"); }
            catch (OperationCanceledException) { }
            Check(f.Source.InfoReads == 0 && !f.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId), "Pre-cancelled static info does not read or take a work lease");
            var read = f.Sampler.GetInfoAsync().AsTask(); var call = await f.Source.NextInfoAsync();
            Check(f.Coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases == 1 && f.Source.Reads == 0,
                "Static info owns real work protection without creating realtime demand");
            var info = Info(); call.Result.TrySetResult(info);
            Check(ReferenceEquals(await read, info) && !f.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId), "Completed static info returns the source DTO and releases protection");
            var failure = f.Sampler.GetInfoAsync().AsTask(); var failed = await f.Source.NextInfoAsync();
            failed.Result.TrySetException(new IOException("test static read"));
            try { await failure; throw new Exception("Info failure hidden"); } catch (IOException) { }
            Check(!f.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId), "Static info failure propagates without leaking or replaying work");
            using var requestCancellation = new CancellationTokenSource();
            var pending = f.Sampler.GetInfoAsync(requestCancellation.Token).AsTask(); var live = await f.Source.NextInfoAsync();
            requestCancellation.Cancel();
            Check(live.Token.IsCancellationRequested && !pending.IsCompleted && f.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId),
                "Request cancellation signals static source but cannot detach a non-cooperative Task");
            using var budget = new CancellationTokenSource(); var stop = f.Sampler.StopAsync(budget.Token); budget.Cancel();
            try { await stop; } catch (OperationCanceledException) { }
            Check(!pending.IsCompleted && f.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId), "Owner stop budget preserves unfinished static info protection");
            try { await f.Sampler.GetInfoAsync(); throw new Exception("Stopped owner accepted info"); }
            catch (ModuleUnavailableException) { }
            Check(f.Source.InfoReads == 3, "Closed owner refuses new static reads before source access");
            live.Result.TrySetResult(Info());
            try { await pending; throw new Exception("Cancelled info returned"); } catch (OperationCanceledException) { }
            await f.Sampler.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            Check(!f.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId), "Actual static read completion drains owner and does not return a cancelled result");
        }
        await using (var f = new Fixture())
        {
            var pending = f.Sampler.GetInfoAsync().AsTask(); var live = await f.Source.NextInfoAsync();
            var stop = f.Coordinator.DisposeAsync().AsTask();
            Check(live.Token.IsCancellationRequested && !stop.IsCompleted, "Coordinator stopping signals static source and waits for its real completion");
            live.Result.TrySetResult(Info());
            try { await pending; throw new Exception("Stopped info returned"); } catch (OperationCanceledException) { }
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            try { await f.Sampler.GetInfoAsync(); throw new Exception("Closed coordinator accepted info"); }
            catch (ModuleUnavailableException) { }
            Check(f.Source.InfoReads == 1 && !f.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId), "Closed coordinator rejects static reads without fallback");
        }
        Console.WriteLine($"Performance residency checks passed: {count}");
    }

    private static PerformanceInfoDto Info() => new(new("test", 1, 2, null, null), new(1024, null), [], [], [],
        new(false, false, false, false, false, false, false, false));
    private static RawPerformanceSample Sample(int n) => new(DateTimeOffset.UtcNow, n * Stopwatch.Frequency,
        new(n * 100, n * 20, null, null, null, [], null), new(1024, 512, null, null, null, null), [], [], [], n);
    private static async Task UntilAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException(); await Task.Delay(10); }
    }
    private sealed class Call(CancellationToken token)
    {
        internal CancellationToken Token => token;
        internal readonly TaskCompletionSource<RawPerformanceSample> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Source : ISystemPerformanceSource
    {
        private readonly Channel<InfoCall> _info = Channel.CreateUnbounded<InfoCall>();
        private readonly System.Collections.Concurrent.ConcurrentBag<InfoCall> _allInfo = new();
        private readonly Channel<Call> _calls = Channel.CreateUnbounded<Call>();
        private readonly System.Collections.Concurrent.ConcurrentBag<Call> _all = new();
        internal int Reads;
        internal int InfoReads;
        public ValueTask<PerformanceInfoDto> GetInfoAsync(CancellationToken ct = default)
        {
            var call = new InfoCall(ct); _allInfo.Add(call); Interlocked.Increment(ref InfoReads); _info.Writer.TryWrite(call);
            return new(call.Result.Task);
        }
        internal Task<InfoCall> NextInfoAsync() => _info.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public ValueTask<RawPerformanceSample> ReadAsync(CancellationToken ct = default)
        {
            var call = new Call(ct); _all.Add(call); Interlocked.Increment(ref Reads); _calls.Writer.TryWrite(call);
            return new(call.Result.Task);
        }
        internal Task<Call> NextAsync() => _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        internal void Complete()
        {
            foreach (var call in _all) call.Result.TrySetResult(Sample(1));
            foreach (var call in _allInfo) call.Result.TrySetResult(Info());
        }
    }
    private sealed class InfoCall(CancellationToken token)
    {
        internal CancellationToken Token => token;
        internal readonly TaskCompletionSource<PerformanceInfoDto> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebApplication _app;
        internal readonly Source Source = new();
        internal readonly PerformanceHistory History = new();
        internal readonly PerformanceSubscriptionRegistry Subscriptions = new();
        internal PerformanceSampler Sampler { get; }
        internal ModuleResidencyCoordinator Coordinator => _app.Services.GetRequiredService<ModuleResidencyCoordinator>();
        internal Fixture()
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            var modules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.SystemPerformanceId });
            modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
            _app = builder.Build();
            Sampler = new(Source, History, Subscriptions, _app.Services.GetRequiredService<ISystemPerformanceWorkLeaseSource>());
        }
        public async ValueTask DisposeAsync()
        {
            var stopped = Sampler.StopAsync(default); Source.Complete();
            await stopped.WaitAsync(TimeSpan.FromSeconds(5)); Sampler.Dispose(); await _app.DisposeAsync();
        }
    }
}
