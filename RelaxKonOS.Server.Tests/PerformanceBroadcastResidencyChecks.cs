using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Protocol.SystemMonitor;
using RelaxKonOS.Server.Hubs;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.SystemPerformance;

internal static class PerformanceBroadcastResidencyChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string text)
        { if (!ok) throw new Exception(text); Console.WriteLine($"PASS BROADCAST RESIDENCY {++count}: {text}"); }
        await using (var f = new Fixture())
        {
            await f.StartAsync();
            Check(!f.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId) && f.Client.Sends == 0,
                "Idle broadcast takes no lease and performs no send");
            f.Sampler.Publish(Snapshot(1)); var first = await f.Client.NextAsync();
            Check(first.Snapshot.Sequence == 1 && f.Coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases == 1,
                "Actual typed Hub send holds a real coordinator work lease");
            for (var i = 2; i <= 10; i++) f.Sampler.Publish(Snapshot(i));
            Check(f.Client.Sends == 1 && f.Coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases == 1,
                "Queued snapshots do not acquire work leases or start parallel sends");
            first.Done.TrySetResult();
            var delivered = new List<long>();
            for (var i = 0; i < 4; i++)
            {
                var call = await f.Client.NextAsync(); delivered.Add(call.Snapshot.Sequence); call.Done.TrySetResult();
            }
            await UntilAsync(() => !f.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId));
            Check(delivered.SequenceEqual([7L, 8L, 9L, 10L]) && f.Client.Sends == 5,
                "Four-slot DropOldest queue preserves only the four newest waiting snapshots");
            await f.Broadcast.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Sampler.Subscribers == 0 && !f.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId),
                "Completed broadcast stop unsubscribes and holds no work protection");
            f.Sampler.Publish(Snapshot(11));
            Check(f.Client.Sends == 5, "Stopped broadcast does not accept later sampler events");
        }
        await using (var f = new Fixture())
        {
            await f.StartAsync(); f.Sampler.Publish(Snapshot(1)); var active = await f.Client.NextAsync();
            for (var i = 2; i <= 6; i++) f.Sampler.Publish(Snapshot(i));
            using var budget = new CancellationTokenSource();
            var stop = f.Broadcast.StopAsync(budget.Token); var coordinatorStop = f.Coordinator.DisposeAsync().AsTask();
            Check(!coordinatorStop.IsCompleted && f.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId),
                "Coordinator shutdown waits for an unfinished non-cooperative typed send");
            budget.Cancel(); try { await stop; } catch (OperationCanceledException) { }
            Check(f.Broadcast.ExecuteTask is { IsCompleted: false } && f.Coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases == 1,
                "Stop wait timeout cannot detach the actual send or release its lease");
            f.Sampler.Publish(Snapshot(99)); active.Done.TrySetResult();
            await f.Broadcast.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            await coordinatorStop.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Client.Sends == 1 && f.Sampler.Subscribers == 0 && !f.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId),
                "Actual send completion precedes drain, unsubscribe and zero leases without sending queued shutdown data");
            Check(f.QueueCount == 0, "Completed shutdown clears all queued snapshot references");
        }
        await using (var f = new Fixture())
        {
            await f.Coordinator.DisposeAsync(); await f.StartAsync(); f.Sampler.Publish(Snapshot(1));
            await UntilAsync(() => f.Broadcast.ExecuteTask?.IsCompleted == true);
            Check(f.Client.Sends == 0 && f.Sampler.Subscribers == 0,
                "Closed coordinator refuses broadcast send without unprotected fallback and removes the handler");
        }
        await using (var f = new Fixture())
        {
            await f.StartAsync(); f.Sampler.Publish(Snapshot(1)); var active = await f.Client.NextAsync();
            f.Broadcast.Dispose(); f.Sampler.Publish(Snapshot(2));
            Check(f.Broadcast.ExecuteTask is { IsCompleted: false } && f.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId),
                "Synchronous disposal closes reception but retains an unfinished send's protection");
            active.Done.TrySetResult(); await UntilAsync(() => f.Broadcast.ExecuteTask?.IsCompleted == true);
            Check(f.Client.Sends == 1 && f.Sampler.Subscribers == 0 && !f.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId),
                "Disposed broadcaster finishes its real send and releases its event ownership");
        }
        await using (var f = new Fixture())
        {
            await f.StartAsync(); f.Sampler.Publish(Snapshot(1)); var active = await f.Client.NextAsync();
            for (var i = 2; i <= 6; i++) f.Sampler.Publish(Snapshot(i));
            active.Done.TrySetException(new IOException("test Hub send"));
            try { await f.Broadcast.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Send failure hidden"); }
            catch (IOException) { }
            Check(f.Client.Sends == 1 && !f.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId),
                "Failed typed send returns protection without replaying or sending queued snapshots");
            Check(f.Sampler.Subscribers == 0 && f.QueueCount == 0,
                "Send failure unsubscribes and clears queued snapshot references before completion");
        }
        Console.WriteLine($"Performance broadcast residency checks passed: {count}");
    }

    private static PerformanceRealtimeSnapshotDto Snapshot(long sequence) => new(sequence, DateTimeOffset.UtcNow,
        new(0, null, null, null, null, [], null, null, null, null), new(1024, 512, 512, null, null, null, null),
        [], [], [], 0, new(false, null, null));
    private static async Task UntilAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException(); await Task.Delay(10); }
    }
    private sealed class Sampler : IPerformanceSampler
    {
        private Action<PerformanceRealtimeSnapshotDto>? _handlers;
        internal int Subscribers => _handlers?.GetInvocationList().Length ?? 0;
        public event Action<PerformanceRealtimeSnapshotDto>? SnapshotAvailable { add => _handlers += value; remove => _handlers -= value; }
        internal void Publish(PerformanceRealtimeSnapshotDto snapshot) => _handlers?.Invoke(snapshot);
        public ValueTask<PerformanceInfoDto> GetInfoAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public PerformanceRealtimeSnapshotDto? GetLatest() => throw new NotSupportedException();
        public IReadOnlyList<PerformanceRealtimeSnapshotDto> GetHistory(int seconds) => throw new NotSupportedException();
        public ValueTask<PerformanceRealtimeSnapshotDto?> ReadSnapshotAsync(TimeSpan wait, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Send(PerformanceRealtimeSnapshotDto snapshot)
    {
        internal PerformanceRealtimeSnapshotDto Snapshot => snapshot;
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Client : IPerformanceHubClient
    {
        private readonly Channel<Send> _calls = Channel.CreateUnbounded<Send>();
        private readonly ConcurrentBag<Send> _all = new();
        internal int Sends;
        public Task OnPerformanceSnapshot(PerformanceRealtimeSnapshotDto snapshot)
        {
            var send = new Send(snapshot); _all.Add(send); Interlocked.Increment(ref Sends); _calls.Writer.TryWrite(send); return send.Done.Task;
        }
        internal Task<Send> NextAsync() => _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        internal void Complete() { foreach (var call in _all) call.Done.TrySetResult(); }
    }
    private sealed class HubContext(Client client) : IHubContext<PerformanceHub, IPerformanceHubClient>
    {
        public IHubClients<IPerformanceHubClient> Clients { get; } = new HubClients(client);
        public IGroupManager Groups => throw new NotSupportedException();
    }
    private sealed class HubClients(Client client) : IHubClients<IPerformanceHubClient>
    {
        public IPerformanceHubClient All => throw new NotSupportedException();
        public IPerformanceHubClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IPerformanceHubClient Client(string connectionId) => throw new NotSupportedException();
        public IPerformanceHubClient Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
        public IPerformanceHubClient Group(string groupName) => groupName == "system-performance" ? client : throw new Exception("Unexpected group");
        public IPerformanceHubClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IPerformanceHubClient Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
        public IPerformanceHubClient User(string userId) => throw new NotSupportedException();
        public IPerformanceHubClient Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebApplication _app;
        internal readonly Sampler Sampler = new();
        internal readonly Client Client = new();
        internal PerformanceBroadcastService Broadcast { get; }
        internal int QueueCount => ((Channel<PerformanceRealtimeSnapshotDto>)typeof(PerformanceBroadcastService)
            .GetField("_queue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(Broadcast)!).Reader.Count;
        internal ModuleResidencyCoordinator Coordinator => _app.Services.GetRequiredService<ModuleResidencyCoordinator>();
        internal Fixture()
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            var modules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.SystemPerformanceId });
            modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
            _app = builder.Build();
            Broadcast = new(Sampler, new HubContext(Client), _app.Services.GetRequiredService<ISystemPerformanceWorkLeaseSource>());
        }
        internal async Task StartAsync() { await Broadcast.StartAsync(default); await UntilAsync(() => Sampler.Subscribers == 1); }
        public async ValueTask DisposeAsync()
        {
            var stop = Broadcast.StopAsync(default); Client.Complete(); await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Broadcast.Dispose(); await _app.DisposeAsync();
        }
    }
}
