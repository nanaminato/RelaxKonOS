using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.SystemMonitor;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.SystemMonitor;
using RelaxKonOS.Server.SystemPerformance;

internal static class ProcessRequestResidencyChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string text)
        { if (!ok) throw new Exception(text); Console.WriteLine($"PASS PROCESS RESIDENCY {++count}: {text}"); }
        await using (var f = new Fixture())
        {
            f.Transport.Snapshot.TrySetResult(Snapshot());
            var first = await f.Sampler.QueryAsync(1, 50, null, null, true, elevated: true);
            Check(first.TotalCount == 1 && f.Source.Acquired == 1 && f.Transport.SnapshotCalls == 1 && f.Coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases == 0,
                "Administrator snapshot uses real protection through materialization and returns its lease");
            var cached = await f.Sampler.QueryAsync(1, 50, null, null, true, elevated: true);
            Check(cached.SampledAt == first.SampledAt && f.Source.Acquired == 2 && f.Transport.SnapshotCalls == 1,
                "A cached query still acquires work protection without making another Helper call");
            await f.Coordinator.DisposeAsync();
            try { await f.Sampler.QueryAsync(1, 50, null, null, true, elevated: true); throw new Exception("Cached data bypassed closed admission"); }
            catch (ModuleUnavailableException) { }
            Check(f.Transport.SnapshotCalls == 1 && f.Sampler.CachedProcessCounts.Elevated == 1,
                "Coordinator rejection cannot bypass admission through a previously cached snapshot");
            await f.Sampler.StopAsync(default);
            Check(f.Sampler.CachedProcessCounts == (0, 0) && cached.Items.Count == 1,
                "Owner shutdown drops cached snapshots and CPU baselines without invalidating caller-owned materialized results");
        }
        await using (var f = new Fixture())
        {
            var first = f.Sampler.QueryAsync(1, 50, null, null, true, elevated: true);
            using var queuedCt = new CancellationTokenSource();
            var waiting = f.Sampler.QueryAsync(1, 50, null, null, true, elevated: true, queuedCt.Token);
            Check(f.Coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases == 2 && f.Transport.SnapshotCalls == 1,
                "Waiting for the existing snapshot gate is also covered by a real lease");
            queuedCt.Cancel();
            try { await waiting; throw new Exception("Waiting query cancellation ignored"); } catch (OperationCanceledException) { }
            Check(f.Coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases == 1 && f.Transport.SnapshotCalls == 1,
                "Cancelling a cache-gate waiter returns only its own lease without starting a second collection");
            f.Transport.Snapshot.TrySetResult(Snapshot()); await first;
        }
        await using (var f = new Fixture())
        using (var client = new CancellationTokenSource())
        {
            var query = f.Sampler.QueryAsync(1, 50, null, null, true, elevated: true, client.Token);
            Check(f.Transport.SnapshotToken == client.Token && f.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId),
                "Snapshot transport keeps the original request cancellation token and a real work lease");
            var ownerStop = f.Sampler.StopAsync(default);
            var coordinatorStop = f.Coordinator.DisposeAsync().AsTask(); client.Cancel();
            Check(!query.IsCompleted && !ownerStop.IsCompleted && !coordinatorStop.IsCompleted,
                "Owner and coordinator cannot finish while a cancelled request still owns an unfinished transport task");
            try { await f.Sampler.QueryAsync(1, 50, null, null, true, elevated: true); throw new Exception("Stopped query accepted"); }
            catch (ModuleUnavailableException) { }
            Check(f.Transport.SnapshotCalls == 1, "Closed owner rejects new queries without invoking Helper");
            using (var budget = new CancellationTokenSource())
            {
                budget.Cancel();
                try { await f.Sampler.StopAsync(budget.Token); throw new Exception("Stop cancellation ignored"); } catch (OperationCanceledException) { }
                Check(f.Coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases == 1,
                    "Cancelling stop wait cannot release a still-running query lease");
            }
            f.Transport.Snapshot.TrySetResult(Snapshot());
            try { await query; throw new Exception("Cancelled snapshot returned success"); } catch (OperationCanceledException) { }
            await Task.WhenAll(ownerStop, coordinatorStop).WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Sampler.CachedProcessCounts == (0, 0) && !f.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId),
                "Cancelled snapshot never populates cache and actual completion precedes final cleanup");
        }
        await using (var f = new Fixture())
        using (var client = new CancellationTokenSource())
        {
            var start = DateTimeOffset.UtcNow.AddTicks(123);
            var kill = f.Sampler.KillAsync(123, start, elevated: true, client.Token);
            Check(f.Transport.TerminateRequest is { ProcessId: 123 } request && request.ProcessExpectedStartTime == start
                && f.Transport.TerminateToken == client.Token && f.Coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases == 1,
                "Termination retains exact PID/start time and original cancellation semantics under a real lease");
            var ownerStop = f.Sampler.StopAsync(default); var coordinatorStop = f.Coordinator.DisposeAsync().AsTask(); client.Cancel();
            Check(!kill.IsCompleted && !ownerStop.IsCompleted && !coordinatorStop.IsCompleted,
                "Cancellation and shutdown cannot discard an unfinished termination task or its protection");
            var unknown = new KillProcessResultDto(false, false, "process.termination_unverified", null);
            f.Transport.Terminated.TrySetResult(new(true, ProcessTermination: unknown));
            Check(await kill == unknown, "Unverified termination receipt is preserved instead of fabricated success or automatic replay");
            await Task.WhenAll(ownerStop, coordinatorStop).WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Transport.TerminateCalls == 1 && f.Coordinator.Describe(ServerModules.SystemPerformanceId)!.ActiveLeases == 0,
                "Termination is issued once and the lease ends only after receipt and cache invalidation");
        }
        await using (var f = new Fixture())
        {
            var kill = f.Sampler.KillAsync(456, DateTimeOffset.UtcNow);
            Check(f.Control.Kills == 1 && f.Transport.TerminateCalls == 0 && f.Coordinator.HasBackgroundWork(ServerModules.SystemPerformanceId),
                "Ordinary termination uses only its existing control port and receives the same module protection");
            var stop = f.Sampler.StopAsync(default);
            f.Control.Result.TrySetResult(new(false, true, "process.permission_denied", null));
            Check((await kill).RequiresElevation, "Ordinary permission failure still requests elevation without secretly invoking Helper");
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await using (var f = new Fixture())
        {
            var invalid = await f.Sampler.KillAsync(0, default, elevated: true);
            Check(invalid.ProblemCode == "process.invalid_instance" && f.Source.Acquired == 0 && f.Transport.TerminateCalls == 0,
                "Invalid termination identity is rejected before lease or external execution");
            await f.Coordinator.DisposeAsync();
            try { await f.Sampler.KillAsync(123, DateTimeOffset.UtcNow, elevated: true); throw new Exception("Closed termination accepted"); }
            catch (ModuleUnavailableException) { }
            Check(f.Control.Kills == 0 && f.Transport.TerminateCalls == 0, "Failed coordinator admission cannot perform either termination path");
        }
        Console.WriteLine($"Process request residency checks passed: {count}");
    }

    private static PrivilegedOperationResult Snapshot()
    {
        var item = new ProcessInfoDto(123, "fixture", 0, 1024, "test", DateTimeOffset.UtcNow, 1, null);
        return new(true, ProcessSnapshot: new([new(item, 100)], DateTimeOffset.UtcNow));
    }

    private sealed class Source(ISystemPerformanceWorkLeaseSource inner) : ISystemPerformanceWorkLeaseSource
    {
        internal int Acquired;
        public async ValueTask<IModuleWorkLease> AcquireAsync(CancellationToken ct)
        { var work = await inner.AcquireAsync(ct); Interlocked.Increment(ref Acquired); return work; }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Transport Transport = new();
        internal readonly Control Control = new();
        private readonly WebApplication _app;
        internal ModuleResidencyCoordinator Coordinator => _app.Services.GetRequiredService<ModuleResidencyCoordinator>();
        internal Source Source { get; }
        internal ProcessSampler Sampler { get; }
        internal Fixture()
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            var modules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.SystemPerformanceId });
            modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
            _app = builder.Build(); Source = new(_app.Services.GetRequiredService<ISystemPerformanceWorkLeaseSource>());
            Sampler = new(Control, new SystemMode(), Transport, Source);
        }
        public async ValueTask DisposeAsync()
        {
            Transport.Snapshot.TrySetResult(ProcessRequestResidencyChecks.Snapshot());
            Transport.Terminated.TrySetResult(new(true, ProcessTermination: new(false, false, "process.termination_unverified", null)));
            Control.Result.TrySetResult(new(false, true, "process.permission_denied", null));
            await Sampler.StopAsync(default); Sampler.Dispose(); await _app.DisposeAsync();
        }
    }
    private sealed class SystemMode : IServerModeResolver
    {
        public ServerMode Mode => ServerMode.System;
        public RelaxKonOS.Protocol.Common.ServerCapabilitiesDto Describe() => throw new NotSupportedException();
        public bool Supports(ServerHostFeature feature) => true;
    }
    private sealed class Control : ISystemMetricsProvider
    {
        internal int Kills;
        internal readonly TaskCompletionSource<KillProcessResultDto> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<SystemMetricsDto> GetMetricsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProcessInfoDto>> ListProcessesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KillProcessResultDto> KillProcessAsync(int pid, DateTimeOffset start, CancellationToken ct = default)
        { Kills++; return Result.Task; }
    }
    private sealed class Transport : IPrivilegedOperationTransport
    {
        internal int SnapshotCalls, TerminateCalls;
        internal CancellationToken SnapshotToken, TerminateToken;
        internal PrivilegedOperationRequest? TerminateRequest;
        internal readonly TaskCompletionSource<PrivilegedOperationResult> Snapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<PrivilegedOperationResult> Terminated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            if (request.Operation == PrivilegedOperationKind.ProcessSnapshot) { SnapshotCalls++; SnapshotToken = ct; return Snapshot.Task; }
            if (request.Operation == PrivilegedOperationKind.ProcessTerminate) { TerminateCalls++; TerminateToken = ct; TerminateRequest = request; return Terminated.Task; }
            throw new Exception("Unexpected Helper operation");
        }
    }
}
