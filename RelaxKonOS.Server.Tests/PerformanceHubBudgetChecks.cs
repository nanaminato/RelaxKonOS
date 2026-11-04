using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Server.Hubs;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.SystemPerformance;

internal static class PerformanceHubBudgetChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        var capacity = PerformanceHubConnections.ConnectionCapacity;
        void Check(bool ok, string text)
        { if (!ok) throw new Exception(text); Console.WriteLine($"PASS HUB BUDGET {++count}: {text}"); }
        await using (var f = new Fixture())
        {
            await Task.WhenAll(Enumerable.Range(0, capacity).Select(i => f.Connect("live-" + i)));
            Check(f.Active == capacity && !f.Subscriptions.NeedsCollection,
                "The connection budget accepts exactly its live capacity without creating sampling demand");
            var acquired = f.Source.Acquisitions;
            await RejectAsync<HubException>(() => f.Connect("overflow"));
            Check(f.Source.Acquisitions == acquired && f.Active == capacity,
                "Live saturation rejects the next connection before work admission");
            await f.Owner.DisconnectAsync("live-0"); await f.Connect("replacement");
            Check(f.Active == capacity, "Actual successful disconnect releases a capacity slot for another connection");
        }
        await using (var f = new Fixture())
        {
            f.Source.Pause();
            using var cancellation = new CancellationTokenSource();
            var cancelled = f.Connect("pending-0", cancellation.Token);
            var pending = Enumerable.Range(1, capacity - 1).Select(i => f.Connect("pending-" + i)).ToArray();
            await UntilAsync(() => f.Active == capacity && f.Source.Acquisitions == capacity);
            await RejectAsync<HubException>(() => f.Connect("overflow"));
            await RejectAsync<HubException>(() => f.Connect("pending-0"));
            Check(f.Source.Acquisitions == capacity && pending.All(task => !task.IsCompleted),
                "Concurrent pending admissions consume the same budget and duplicate pending IDs never acquire another lease");
            cancellation.Cancel();
            await RejectAsync<HubException>(() => f.Connect("still-full"));
            Check(!cancelled.IsCompleted && f.Active == capacity && f.Source.Acquisitions == capacity,
                "Cancelling non-cooperative pending admission does not prematurely release its capacity or actual protection");
            f.Source.Complete();
            await RejectAsync<OperationCanceledException>(() => cancelled); await Task.WhenAll(pending);
            await f.Connect("returned-slot");
            Check(f.Active == capacity && !f.Subscriptions.NeedsCollection,
                "Late cancelled admission cannot become a connection and releases its slot only after returning its real lease");
        }
        await using (var f = new Fixture())
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await RejectAsync<OperationCanceledException>(() => f.Connect("pre-cancelled", cancellation.Token));
            Check(f.Source.Acquisitions == 0 && f.Active == 0, "Pre-cancelled connection reserves no lasting capacity and never acquires work");
            f.Source.FailNext = true;
            await RejectAsync<IOException>(() => f.Connect("retry-id"));
            await f.Connect("retry-id");
            await Task.WhenAll(Enumerable.Range(1, capacity - 1).Select(i => f.Connect("after-failure-" + i)));
            Check(f.Active == capacity && f.Source.Acquisitions == capacity + 1,
                "Lease admission failure releases its identity and capacity so a full later batch can still enter");
        }
        await using (var f = new Fixture())
        {
            f.Source.Pause(); var connection = f.Connect("stopping-pending");
            await UntilAsync(() => f.Active == 1);
            using var budget = new CancellationTokenSource(); var stop = f.Owner.StopAsync(budget.Token); budget.Cancel();
            await RejectAsync<OperationCanceledException>(() => stop);
            Check(!connection.IsCompleted && f.Active == 1,
                "Stop wait timeout preserves pending admission ownership and its actual lease");
            f.Source.Complete();
            await RejectAsync<ModuleUnavailableException>(() => connection);
            await f.Owner.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Active == 0 && !f.Subscriptions.NeedsCollection,
                "Actual pending completion after stop returns the reserved slot without publishing a live connection");
        }
        // Retained unknown cleanup is intentionally not recovered. Counting leases avoid a real shutdown drain timeout.
        var leases = new TestSystemPerformanceWorkLeases(); var registry = new PerformanceSubscriptionRegistry();
        var owner = new PerformanceHubConnections(leases, registry); var groups = new Groups();
        await Task.WhenAll(Enumerable.Range(0, capacity).Select(i => owner.ConnectAsync("fault-" + i, groups, () => { }, default)));
        await owner.SubscribeAsync("fault-0"); groups.FailedId = "fault-0";
        await RejectAsync<IOException>(() => owner.DisconnectAsync("fault-0"));
        var before = leases.Acquired;
        await RejectAsync<HubException>(() => owner.ConnectAsync("fault-overflow", groups, () => { }, default));
        Check(leases.Active == capacity && leases.Acquired == before && groups.Removes == 1 && !registry.NeedsCollection,
            "Unknown cleanup retains its capacity slot and lease rather than admitting an extra connection or replaying Remove");
        await owner.DisconnectAsync("fault-1");
        await owner.ConnectAsync("fault-replacement", groups, () => { }, default);
        Check(leases.Active == capacity, "Healthy cleanup still returns its own slot while a different fault remains owned");
        await RejectAsync<IOException>(() => owner.StopAsync(default));
        Check(leases.Active == 1 && groups.Removes == 1,
            "Stop reports the retained fault and drains healthy slots without falsely freeing or retrying failed cleanup");
        owner.Dispose();
        Console.WriteLine($"Performance Hub budget checks passed: {count}");
    }

    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    private static async Task UntilAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException(); await Task.Delay(10); }
    }
    private sealed class Groups : IGroupManager
    {
        internal string? FailedId;
        internal int Removes;
        public Task AddToGroupAsync(string id, string group, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string id, string group, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Removes);
            return id == FailedId ? Task.FromException(new IOException("test unknown Remove")) : Task.CompletedTask;
        }
    }
    private sealed class Source(ISystemPerformanceWorkLeaseSource inner) : ISystemPerformanceWorkLeaseSource
    {
        internal int Acquisitions;
        internal bool FailNext;
        private TaskCompletionSource? _return;
        internal void Pause() => _return = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Complete() => _return?.TrySetResult();
        public async ValueTask<IModuleWorkLease> AcquireAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Acquisitions);
            if (FailNext) { FailNext = false; throw new IOException("test admission failure"); }
            var lease = await inner.AcquireAsync(ct);
            if (_return is { } pending) await pending.Task;
            return lease;
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebApplication _app;
        internal readonly Groups Groups = new();
        internal PerformanceHubConnections Owner => _app.Services.GetRequiredService<PerformanceHubConnections>();
        internal PerformanceSubscriptionRegistry Subscriptions => _app.Services.GetRequiredService<PerformanceSubscriptionRegistry>();
        internal ModuleResidencyCoordinator Coordinator => _app.Services.GetRequiredService<ModuleResidencyCoordinator>();
        internal Source Source => (Source)_app.Services.GetRequiredService<ISystemPerformanceWorkLeaseSource>();
        internal int Active => Coordinator.Describe(ServerModules.SystemPerformanceId)?.ActiveLeases ?? 0;
        internal Fixture()
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            var modules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.SystemPerformanceId });
            modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
            builder.Services.RemoveAll<ISystemPerformanceWorkLeaseSource>();
            builder.Services.AddSingleton<ISystemPerformanceWorkLeaseSource>(services => new Source(new SystemPerformanceWorkLeaseSource(services.GetRequiredService<ModuleResidencyCoordinator>())));
            _app = builder.Build();
        }
        internal Task Connect(string id, CancellationToken ct = default) => Owner.ConnectAsync(id, Groups, () => { }, ct);
        public async ValueTask DisposeAsync()
        {
            var stop = Owner.StopAsync(default); Source.Complete();
            await stop.WaitAsync(TimeSpan.FromSeconds(5)); await _app.DisposeAsync();
        }
    }
}
