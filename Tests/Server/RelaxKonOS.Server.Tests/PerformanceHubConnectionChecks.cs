using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Server.Hubs;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.SystemPerformance;

internal static class PerformanceHubConnectionChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string text)
        { if (!ok) throw new Exception(text); Console.WriteLine($"PASS HUB CONNECTION {++count}: {text}"); }
        await using (var f = new Fixture())
        {
            using var context = new Context("hub");
            var hub = new PerformanceHub(f.Owner) { Context = context, Groups = f.Groups };
            await hub.OnConnectedAsync();
            Check(f.Active == 1 && !f.Subscriptions.NeedsCollection, "Actual Hub connect holds continuous protection before subscribing without starting collection");
            await hub.Subscribe(); await hub.Subscribe();
            Check(f.Active == 1 && f.Groups.Adds == 1 && f.Subscriptions.NeedsCollection, "Repeated Hub subscribe is idempotent and retains the single connection lease");
            await hub.Unsubscribe(); await hub.Unsubscribe();
            Check(f.Active == 1 && f.Groups.Removes == 1 && !f.Subscriptions.NeedsCollection, "Unsubscribe removes membership and demand while connected lifetime remains protected");
            await hub.Subscribe(); await hub.OnDisconnectedAsync(null); await hub.OnDisconnectedAsync(null);
            Check(f.Active == 0 && f.Groups.Removes == 2 && !f.Subscriptions.NeedsCollection, "Actual Hub disconnect cleans up membership exactly once and returns its continuous lease");
        }
        await using (var f = new Fixture())
        {
            await f.Connect("same");
            try { await f.Connect("same"); throw new Exception("Duplicate accepted"); } catch (HubException) { }
            Check(f.Active == 1, "Duplicate connection identity cannot replace or leak the original protected owner");
            await f.Owner.DisconnectAsync("same");
            Check(f.Active == 0, "Original connection remains removable after rejected duplicate admission");
            using var ct = new CancellationTokenSource(); ct.Cancel();
            try { await f.Owner.ConnectAsync("cancelled", f.Groups, () => { }, ct.Token); throw new Exception("Precancelled connect accepted"); }
            catch (OperationCanceledException) { }
            Check(f.Active == 0 && f.Groups.Adds == 0, "Precancelled connection creates neither membership nor lasting work");
        }
        await using (var f = new Fixture())
        {
            await f.Connect("first"); await f.Connect("second");
            await f.Owner.SubscribeAsync("first"); await f.Owner.SubscribeAsync("second");
            await f.Owner.UnsubscribeAsync("first");
            Check(f.Active == 2 && f.Subscriptions.NeedsCollection && f.Groups.Adds == 2 && f.Groups.Removes == 1,
                "Distinct connections independently hold leases and one unsubscribe cannot end another connection's demand");
            await f.Owner.DisconnectAsync("first"); await f.Owner.DisconnectAsync("second");
            await f.Connect("first");
            Check(f.Active == 1 && !f.Subscriptions.NeedsCollection,
                "Completed disconnect removes its entry so the same identity can connect again with fresh protection");
            await f.Owner.DisconnectAsync("first");
        }
        await using (var f = new Fixture())
        {
            await f.Connect("late");
            var pendingAdd = f.Groups.BlockAdd();
            var subscribe = f.Owner.SubscribeAsync("late");
            await UntilAsync(() => f.Groups.Adds == 1);
            var disconnect = f.Owner.DisconnectAsync("late");
            await UntilAsync(() => f.Aborts == 1);
            Check(!disconnect.IsCompleted && f.Active == 1, "Disconnect aborts connection but cannot detach an unfinished Add task");
            pendingAdd.TrySetResult();
            try { await subscribe; throw new Exception("Late subscribe succeeded"); } catch (HubException) { }
            await disconnect.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!f.Subscriptions.NeedsCollection && f.Groups.Removes == 1 && f.Active == 0, "Late Add cannot resurrect demand and is removed before its connection lease ends");
        }
        await using (var f = new Fixture())
        {
            await f.Connect("stop"); await f.Owner.SubscribeAsync("stop");
            var pendingRemove = f.Groups.BlockRemove();
            using var budget = new CancellationTokenSource();
            var stop = f.Owner.StopAsync(budget.Token);
            await UntilAsync(() => f.Groups.Removes == 1);
            budget.Cancel();
            try { await stop; } catch (OperationCanceledException) { }
            Check(f.Aborts == 1 && f.Active == 1 && !f.Subscriptions.NeedsCollection, "Host stop withdraws demand but preserves protection while real group cleanup is unfinished");
            try { await f.Connect("closed"); throw new Exception("Stopped owner accepted"); } catch (ModuleUnavailableException) { }
            Check(f.Active == 1, "Closed owner rejects new connection admission without replacing in-flight protection");
            pendingRemove.TrySetResult(); await f.Owner.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Active == 0 && f.Groups.Removes == 1, "Completed group cleanup drains host owner without replaying Remove");
        }
        await using (var f = new Fixture())
        {
            await f.Connect("coordinator"); await f.Owner.SubscribeAsync("coordinator");
            var pendingRemove = f.Groups.BlockRemove();
            var stop = f.Coordinator.DisposeAsync().AsTask();
            await UntilAsync(() => f.Groups.Removes == 1);
            Check(f.Aborts == 1 && !stop.IsCompleted && f.Active == 1, "Coordinator stop aborts a subscribed connection and waits for its actual cleanup");
            pendingRemove.TrySetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Owner.DisconnectAsync("coordinator");
            Check(f.Active == 0 && !f.Subscriptions.NeedsCollection, "Coordinator drain completes only after connection cleanup and demand withdrawal");
            try { await f.Connect("unavailable"); throw new Exception("Closed coordinator accepted"); } catch (ModuleUnavailableException) { }
            Check(f.Active == 0, "Closed coordinator rejects connections without unprotected fallback");
        }
        await using (var f = new Fixture())
        {
            await f.Connect("removing"); await f.Owner.SubscribeAsync("removing");
            var pendingRemove = f.Groups.BlockRemove(); var unsubscribe = f.Owner.UnsubscribeAsync("removing");
            await UntilAsync(() => f.Groups.Removes == 1);
            var stop = f.Coordinator.DisposeAsync().AsTask();
            await UntilAsync(() => f.Aborts == 1);
            Check(!unsubscribe.IsCompleted && !stop.IsCompleted && f.Active == 1 && !f.Groups.LastRemoveToken.CanBeCanceled,
                "Coordinator stopping cannot cancel or detach an already-started explicit Remove task");
            pendingRemove.TrySetResult(); await unsubscribe; await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Active == 0 && f.Groups.Removes == 1 && !f.Subscriptions.NeedsCollection,
                "Started Remove finishes once before connection drain without a second cleanup Remove");
        }
        await using (var f = new Fixture())
        {
            using var disconnected = new CancellationTokenSource();
            await f.Owner.ConnectAsync("token", f.Groups, () => Interlocked.Increment(ref f.Aborts), disconnected.Token);
            await f.Owner.SubscribeAsync("token"); disconnected.Cancel();
            await UntilAsync(() => f.Active == 0);
            Check(f.Aborts == 1 && f.Groups.Removes == 1 && !f.Subscriptions.NeedsCollection, "Connection-aborted token cleans up without relying on a later Hub callback");
        }
        await using (var f = new Fixture())
        {
            await f.Connect("failed-add");
            f.Groups.AddFailure = new IOException("test Add outcome");
            try { await f.Owner.SubscribeAsync("failed-add"); throw new Exception("Add failure hidden"); } catch (IOException) { }
            await f.Owner.DisconnectAsync("failed-add").WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Groups.Adds == 1 && f.Groups.Removes == 1 && !f.Subscriptions.NeedsCollection && f.Active == 0,
                "Failed Add is not replayed; final cleanup removes possible membership before releasing protection");
        }
        // Unknown Remove responsibility intentionally remains owned. A fake lease avoids a real coordinator drain timeout.
        var leases = new TestSystemPerformanceWorkLeases(); var registry = new PerformanceSubscriptionRegistry();
        var failedOwner = new PerformanceHubConnections(leases, registry); var failedGroups = new Groups();
        await failedOwner.ConnectAsync("failed-remove", failedGroups, () => { }, default);
        await failedOwner.SubscribeAsync("failed-remove"); failedGroups.RemoveFailure = new IOException("test Remove outcome");
        try { await failedOwner.UnsubscribeAsync("failed-remove"); throw new Exception("Remove failure hidden"); } catch (IOException) { }
        try { await failedOwner.DisconnectAsync("failed-remove"); throw new Exception("Cleanup failure hidden"); } catch (IOException) { }
        Check(leases.Active == 1 && !registry.NeedsCollection && failedGroups.Removes == 1, "Unknown Remove outcome withdraws demand while retaining fault responsibility and its lease without replay");
        try { await failedOwner.StopAsync(default); throw new Exception("Stop hid cleanup failure"); } catch (IOException) { }
        Check(leases.Active == 1 && failedGroups.Removes == 1, "Stop reports retained cleanup failure and does not falsely release or retry it");
        failedOwner.Dispose();
        var finalLeases = new TestSystemPerformanceWorkLeases(); var finalRegistry = new PerformanceSubscriptionRegistry();
        var finalOwner = new PerformanceHubConnections(finalLeases, finalRegistry); var finalGroups = new Groups();
        await finalOwner.ConnectAsync("failed-final-remove", finalGroups, () => { }, default);
        await finalOwner.SubscribeAsync("failed-final-remove"); finalGroups.RemoveFailure = new IOException("test final Remove outcome");
        try { await finalOwner.DisconnectAsync("failed-final-remove"); throw new Exception("Final Remove failure hidden"); } catch (IOException) { }
        Check(finalLeases.Active == 1 && !finalRegistry.NeedsCollection && finalGroups.Removes == 1,
            "Final disconnect Remove failure retains its lease and fault while withdrawing sampling demand");
        try { await finalOwner.StopAsync(default); throw new Exception("Stop hid final Remove failure"); } catch (IOException) { }
        Check(finalLeases.Active == 1 && finalGroups.Removes == 1,
            "Stop surfaces failed final disconnect cleanup without replaying the unknown Remove");
        finalOwner.Dispose();
        Console.WriteLine($"Performance Hub connection checks passed: {count}");
    }

    private static async Task UntilAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException(); await Task.Delay(10); }
    }
    private sealed class Groups : IGroupManager
    {
        internal int Adds, Removes;
        internal CancellationToken LastRemoveToken;
        internal Exception? AddFailure, RemoveFailure;
        private TaskCompletionSource? _add, _remove;
        internal TaskCompletionSource BlockAdd() => _add = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource BlockRemove() => _remove = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            if (groupName != "system-performance") throw new Exception("Unexpected group");
            Interlocked.Increment(ref Adds);
            if (_add is { } pending) await pending.Task;
            if (AddFailure is { } failure) throw failure;
        }
        public async Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            if (groupName != "system-performance") throw new Exception("Unexpected group");
            LastRemoveToken = cancellationToken;
            Interlocked.Increment(ref Removes);
            if (_remove is { } pending) await pending.Task;
            if (RemoveFailure is { } failure) throw failure;
        }
        internal void Complete() { _add?.TrySetResult(); _remove?.TrySetResult(); }
    }
    private sealed class Context(string id) : HubCallerContext, IDisposable
    {
        private readonly CancellationTokenSource _aborted = new();
        public override string ConnectionId => id;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => _aborted.Token;
        public override void Abort() => _aborted.Cancel();
        public void Dispose() => _aborted.Dispose();
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebApplication _app;
        internal readonly Groups Groups = new();
        internal int Aborts;
        internal PerformanceHubConnections Owner => _app.Services.GetRequiredService<PerformanceHubConnections>();
        internal PerformanceSubscriptionRegistry Subscriptions => _app.Services.GetRequiredService<PerformanceSubscriptionRegistry>();
        internal ModuleResidencyCoordinator Coordinator => _app.Services.GetRequiredService<ModuleResidencyCoordinator>();
        internal int Active => Coordinator.Describe(ServerModules.SystemPerformanceId)?.ActiveLeases ?? 0;
        internal Fixture()
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            var modules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.SystemPerformanceId });
            modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
            _app = builder.Build();
        }
        internal Task Connect(string id) => Owner.ConnectAsync(id, Groups, () => Interlocked.Increment(ref Aborts), default);
        public async ValueTask DisposeAsync()
        {
            var stop = Owner.StopAsync(default); Groups.Complete();
            await stop.WaitAsync(TimeSpan.FromSeconds(5)); await _app.DisposeAsync();
        }
    }
}
