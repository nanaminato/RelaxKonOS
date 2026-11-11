using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.SystemPerformance;

internal static class ProcessIconWorkChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string text)
        { if (!ok) throw new Exception(text); Console.WriteLine($"PASS ICON WORK {++count}: {text}"); }
        var released = 0;
        var queue = new ProcessIconWorkQueue(() => Interlocked.Increment(ref released));
        var finish = new TaskCompletionSource<ProcessIconContent?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        Task<ProcessIconContent?> Work() { Interlocked.Increment(ref started); return finish.Task; }
        var active = Enumerable.Range(0, ProcessIconWorkQueue.MaximumParallel).Select(_ => queue.RunAsync(Work, default)).ToArray();
        Check(started == 4 && queue.Describe() == (4, 4, false), "Four operations can start without creating an unbounded Task.Run queue");
        using var cancellation = new CancellationTokenSource();
        var cancelled = queue.RunAsync(Work, cancellation.Token);
        var waiting = Enumerable.Range(0, ProcessIconWorkQueue.MaximumWaiting - 1).Select(_ => queue.RunAsync(Work, default)).ToArray();
        Check(queue.Describe() == (4, 36, false) && started == 4, "Exactly 32 additional requests wait without invoking their work");
        Check(await queue.RunAsync(() => throw new Exception("Overload started"), default) is null && queue.Describe().Pending == 36,
            "Overload returns optional-icon fallback before starting or retaining another operation");
        cancellation.Cancel();
        try { await cancelled; throw new Exception("Queued cancellation ignored"); } catch (OperationCanceledException) { }
        Check(started == 4 && queue.Describe().Pending == 35, "Queued cancellation removes its claim without starting native work");
        var replacement = queue.RunAsync(Work, default);
        var stop = queue.StopAsync(default);
        await Task.WhenAll(waiting.Append(replacement)).WaitAsync(TimeSpan.FromSeconds(5));
        Check(waiting.All(task => task.Result is null) && replacement.Result is null && started == 4,
            "Shutdown wakes queued requests without executing them");
        Check(!stop.IsCompleted && released == 0 && queue.Describe() == (4, 4, true), "Shutdown retains active work and resources until actual operation completion");
        Check(await queue.RunAsync(() => throw new Exception("Late started"), default) is null, "Closed admission never starts a new request");
        using (var budget = new CancellationTokenSource())
        {
            budget.Cancel();
            try { await queue.StopAsync(budget.Token); throw new Exception("Cancelled stop succeeded"); } catch (OperationCanceledException) { }
            Check(released == 0 && queue.Describe().Active == 4, "Stop cancellation ends only the wait and cannot release active ownership");
        }
        finish.TrySetResult(new([7], "image/png"));
        await Task.WhenAll(active).WaitAsync(TimeSpan.FromSeconds(5)); await stop.WaitAsync(TimeSpan.FromSeconds(5));
        queue.BeginStop(); await queue.StopAsync(default);
        Check(released == 1 && queue.Describe() == (0, 0, true), "Last operation completion releases resources exactly once and repeated stop stays idempotent");

        released = 0;
        var native = new ProcessIconWorkQueue(() => Interlocked.Increment(ref released));
        using (var request = new CancellationTokenSource())
        using (var releaseNative = new ManualResetEventSlim())
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var executing = native.RunAsync(() => Task.Run<ProcessIconContent?>(() =>
            { entered.TrySetResult(); if (!releaseNative.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return new([8], "image/png"); }), request.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); request.Cancel();
            var draining = native.StopAsync(default);
            Check(!executing.IsCompleted && !draining.IsCompleted && native.Describe().Active == 1 && released == 0,
                "Cancellation cannot detach a started synchronous worker or pretend its native resources have drained");
            releaseNative.Set();
            try { await executing; throw new Exception("Started cancellation ignored"); } catch (OperationCanceledException) { }
            await draining.WaitAsync(TimeSpan.FromSeconds(5));
            Check(native.Describe().Active == 0 && released == 1, "Caller cancellation is reported after the real worker ends and cleanup runs");
        }
        var failed = new ProcessIconWorkQueue(() => { });
        try { await failed.RunAsync(() => throw new IOException("test load"), default); throw new Exception("Work error ignored"); } catch (IOException) { }
        Check((await failed.RunAsync(() => Task.FromResult<ProcessIconContent?>(new([9], "image/png")), default))?.Data[0] == 9
            && failed.Describe().Pending == 0, "Failed work returns its admission slot without poisoning later reads");
        await failed.StopAsync(default);
        var cleanupAttempts = 0;
        var cleanupFailure = new ProcessIconWorkQueue(() => { cleanupAttempts++; throw new IOException("cleanup failed"); });
        for (var i = 0; i < 2; i++)
        { try { await cleanupFailure.StopAsync(default); throw new Exception("Cleanup failure ignored"); } catch (IOException) { } }
        Check(cleanupAttempts == 1 && cleanupFailure.Describe().Stopping, "Failed cleanup remains visible on stop and is never replayed or reopened");

        var retainedQueue = new ProcessIconWorkQueue(() => { });
        var retained = StartRetainedWork(retainedQueue);
        retained.Release.TrySetResult(); await retained.Work.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; retained.Payload.IsAlive && i < 30; i++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(10); }
        Check(!retained.Payload.IsAlive, "Retained owner and completed request task do not retain the finished work closure payload");
        await retainedQueue.StopAsync(default); GC.KeepAlive(retainedQueue); GC.KeepAlive(retained);

        var transport = new IconTransport();
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(_ => new ProcessSampler(null!, null!, transport, new TestSystemPerformanceWorkLeases()));
            services.AddHostedService(services => services.GetRequiredService<ProcessSampler>());
        }).Build();
        await host.StartAsync();
        var sampler = host.Services.GetRequiredService<ProcessSampler>();
        using var client = new CancellationTokenSource();
        var icon = sampler.GetIconAsync(123, DateTimeOffset.UtcNow, elevated: true, client.Token);
        var others = Enumerable.Range(0, 3).Select(_ => sampler.GetIconAsync(123, DateTimeOffset.UtcNow, elevated: true)).ToArray();
        var ordinaryWaiting = sampler.GetIconAsync(Environment.ProcessId, DateTimeOffset.UtcNow);
        var adminWaiting = Enumerable.Range(0, 31).Select(_ => sampler.GetIconAsync(123, DateTimeOffset.UtcNow, elevated: true)).ToArray();
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); client.Cancel();
        Check(transport.Calls == 4 && !ordinaryWaiting.IsCompleted && adminWaiting.All(task => !task.IsCompleted),
            "Real ordinary and administrator ProcessSampler requests share four active and 32 waiting claims");
        Check(await sampler.GetIconAsync(123, DateTimeOffset.UtcNow, elevated: true) is null && transport.Calls == 4,
            "Real ProcessSampler overload cannot start a fifth helper call");
        var hostStop = host.StopAsync();
        Check(!hostStop.IsCompleted && !icon.IsCompleted && !transport.Token.CanBeCanceled,
            "Real Host.StopAsync waits for accepted administrator icon transport after request cancellation");
        Check(await sampler.GetIconAsync(123, DateTimeOffset.UtcNow, elevated: true) is null && transport.Calls == 4,
            "Stopped ProcessSampler refuses further helper icon calls without altering the accepted one");
        transport.Release.TrySetResult();
        try { await icon; throw new Exception("Client cancellation lost"); } catch (OperationCanceledException) { }
        await Task.WhenAll(others.Concat(adminWaiting).Append(ordinaryWaiting)).WaitAsync(TimeSpan.FromSeconds(5));
        await hostStop.WaitAsync(TimeSpan.FromSeconds(5));
        Check(transport.Calls == 4 && ordinaryWaiting.Result is null && adminWaiting.All(task => task.Result is null),
            "Accepted helper reads are never replayed and queued ordinary/admin work never starts during shutdown");
        Console.WriteLine($"Process icon work checks passed: {count}");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (Task<ProcessIconContent?> Work, TaskCompletionSource Release, WeakReference Payload) StartRetainedWork(ProcessIconWorkQueue queue)
    {
        var payload = new byte[1024 * 1024];
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = queue.RunAsync(async () => { await release.Task; GC.KeepAlive(payload); return null; }, default);
        return (work, release, new WeakReference(payload));
    }

    private sealed class IconTransport : IPrivilegedOperationTransport
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token;
        internal int Calls;
        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Operation != PrivilegedOperationKind.ProcessIcon) throw new Exception("Unexpected helper operation");
            Calls++; Token = cancellationToken; Entered.TrySetResult(); await Release.Task;
            return new(true, OutputBase64: "AQ==", ProcessIconContentType: "image/png");
        }
    }
}
