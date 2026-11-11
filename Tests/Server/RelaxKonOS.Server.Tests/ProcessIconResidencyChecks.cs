using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.SystemPerformance;

internal static class ProcessIconResidencyChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string text)
        { if (!ok) throw new Exception(text); Console.WriteLine($"PASS ICON RESIDENCY {++count}: {text}"); }
        await using (var fixture = new Fixture())
        {
            var coordinator = fixture.Coordinator;
            var sampler = fixture.Sampler;
            Check(coordinator.Describe(ServerModules.SystemPerformanceId) is { State: ModuleResidencyState.Resident, Releasable: false, ActiveLeases: 0 }
                && !fixture.Modules.KnownReleasableModuleIds.Contains(ServerModules.SystemPerformanceId),
                "Actual SystemPerformance module registers a resident work owner without advertising sleep");
            Check(await coordinator.TrySleepAsync(ServerModules.SystemPerformanceId, default) == "module-resident-work-owner",
                "Unused performance work metadata cannot report successful module sleep");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try { await sampler.GetIconAsync(123, DateTimeOffset.UtcNow, elevated: true, cancelled.Token); throw new Exception("Cancelled request accepted"); }
                catch (OperationCanceledException) { }
                Check(fixture.Transport.Calls == 0 && coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases == 0,
                    "Pre-cancelled icon requests never acquire a lease or contact Helper");
            }
            using var client = new CancellationTokenSource();
            var first = sampler.GetIconAsync(123, DateTimeOffset.UtcNow, elevated: true, client.Token);
            var active = Enumerable.Range(0, 3).Select(_ => sampler.GetIconAsync(123, DateTimeOffset.UtcNow, elevated: true)).ToArray();
            await UntilAsync(() => fixture.Transport.Calls == 4);
            Check(coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId)
                && coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases == 4,
                "Real coordinator work leases protect all four accepted administrator reads");
            using var queuedCancellation = new CancellationTokenSource();
            var cancelledWaiter = sampler.GetIconAsync(123, DateTimeOffset.UtcNow, elevated: true, queuedCancellation.Token);
            var queued = Enumerable.Range(0, 31).Select(_ => sampler.GetIconAsync(123, DateTimeOffset.UtcNow, elevated: true)).ToArray();
            await UntilAsync(() => coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases == 36);
            Check(fixture.Transport.Calls == 4, "All 32 queued requests hold real work protection before waiting for execution slots");
            Check(await sampler.GetIconAsync(123, DateTimeOffset.UtcNow, elevated: true) is null
                && coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases == 36,
                "Overload neither acquires an extra coordinator lease nor starts another Helper read");
            queuedCancellation.Cancel();
            try { await cancelledWaiter; throw new Exception("Queue cancellation ignored"); } catch (OperationCanceledException) { }
            Check(coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases == 35 && fixture.Transport.Calls == 4,
                "Cancelling queued work returns its lease without executing the request");
            client.Cancel();
            Check(!first.IsCompleted && coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases == 35,
                "Cancelling started work cannot return its real lease before transport completion");
            var coordinatorDrain = coordinator.DisposeAsync().AsTask();
            await Task.WhenAll(queued).WaitAsync(TimeSpan.FromSeconds(5));
            Check(fixture.Transport.Calls == 4 && coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases == 4,
                "Coordinator stopping token cancels queued protected reads before sampler Stop is called");
            var drain = sampler.StopAsync(default);
            Check(queued.All(task => task.Result is null) && !drain.IsCompleted && !coordinatorDrain.IsCompleted
                && coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases == 4,
                "Sampler and coordinator both wait for the actual four reads after queued leases are released");
            using (var stoppedWait = new CancellationTokenSource())
            {
                stoppedWait.Cancel();
                try { await sampler.StopAsync(stoppedWait.Token); throw new Exception("Stop wait ignored cancellation"); } catch (OperationCanceledException) { }
                Check(coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases == 4 && !coordinatorDrain.IsCompleted,
                    "Cancelling stop wait cannot clear coordinator protection for live reads");
            }
            fixture.Transport.Release.TrySetResult();
            try { await first; throw new Exception("Active cancellation ignored"); } catch (OperationCanceledException) { }
            await Task.WhenAll(active).WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(drain, coordinatorDrain).WaitAsync(TimeSpan.FromSeconds(5));
            Check(!coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId)
                && coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases == 0 && fixture.Transport.Calls == 4,
                "Actual read completion precedes work lease release and neither shutdown path replays Helper");
        }
        await using (var fixture = new Fixture())
        {
            await fixture.Coordinator.DisposeAsync();
            try { await fixture.Sampler.GetIconAsync(123, DateTimeOffset.UtcNow, elevated: true); throw new Exception("Unavailable owner accepted work"); }
            catch (ModuleUnavailableException) { }
            Check(fixture.Transport.Calls == 0 && !fixture.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId),
                "Coordinator admission failure refuses the icon before any Helper or native execution");
        }
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { [$"ModuleResidency:Modules:{ServerModules.SystemPerformanceId}:Strategy"] = "IdleDelayed" });
        try
        {
            var modules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.SystemPerformanceId });
            try { modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory")); throw new Exception("Resident idle policy accepted"); }
            catch (InvalidOperationException error) when (error.Message.Contains("does not declare a release boundary", StringComparison.Ordinal))
            { Check(true, "Adding performance work ownership does not enable an idle-release policy"); }
        }
        finally { ((IDisposable)builder.Configuration).Dispose(); }
        Console.WriteLine($"Process icon residency checks passed: {count}");
    }

    private static async Task UntilAsync(Func<bool> done)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!done()) { if (DateTime.UtcNow >= timeout) throw new TimeoutException("Icon lease state did not converge"); await Task.Delay(10); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Transport Transport = new();
        private readonly WebApplication _app;
        internal ServerModules Modules { get; }
        internal ModuleResidencyCoordinator Coordinator => _app.Services.GetRequiredService<ModuleResidencyCoordinator>();
        internal ProcessSampler Sampler => _app.Services.GetRequiredService<ProcessSampler>();
        internal Fixture()
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            Modules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.SystemPerformanceId });
            Modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
            builder.Services.AddSingleton(services => new ProcessSampler(null!, null!, Transport, services.GetRequiredService<ISystemPerformanceWorkLeaseSource>()));
            _app = builder.Build();
        }
        public async ValueTask DisposeAsync() { Transport.Release.TrySetResult(); await _app.DisposeAsync(); }
    }

    private sealed class Transport : IPrivilegedOperationTransport
    {
        internal int Calls;
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            if (request.Operation != PrivilegedOperationKind.ProcessIcon) throw new Exception("Unexpected operation");
            Interlocked.Increment(ref Calls); await Release.Task;
            return new(true, OutputBase64: "AQ==", ProcessIconContentType: "image/png");
        }
    }
}
