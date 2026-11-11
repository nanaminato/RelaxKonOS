using System.Runtime.CompilerServices;
using RelaxKonOS.Server.SystemPerformance;

internal static class ProcessIconCacheChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string text)
        { if (!ok) throw new Exception(text); Console.WriteLine($"PASS ICON CACHE {++count}: {text}"); }
        using (var cache = new ProcessIconCache())
        {
            var loads = 0;
            var results = new ProcessIconContent[64];
            Parallel.For(0, results.Length, index => results[index] = cache.GetOrAdd("same-instance", () =>
            { Interlocked.Increment(ref loads); return new(new byte[1024], "image/png"); }));
            Check(loads == 1 && results.All(item => ReferenceEquals(item, results[0])), "Concurrent same-key reads share one load and payload");
            Check(cache.Describe() == (1, 1024, 1), "Deduplication creates exactly one order node and accounts payload once");
            for (var i = 0; i < 600; i++) cache.GetOrAdd("missing-" + i, () => new([], "image/png"));
            Check(cache.Describe() == (ProcessIconCache.MaximumEntries, 0, ProcessIconCache.MaximumEntries), "Negative entries and order nodes obey the 512 entry limit and evict old payload");
        }
        using (var cache = new ProcessIconCache())
        {
            for (var i = 0; i < 40; i++) cache.GetOrAdd("svg-" + i, () => new(new byte[262144], "image/svg+xml"));
            Check(cache.Describe() == (32, ProcessIconCache.MaximumBytes, 32), "Large SVG payloads stop at 8 MiB rather than reaching 512 times 256 KiB");
            var reloaded = false;
            cache.GetOrAdd("svg-0", () => { reloaded = true; return new([1], "image/png"); });
            Check(reloaded && cache.Describe().Bytes <= ProcessIconCache.MaximumBytes, "FIFO eviction reloads the oldest executable without exceeding the byte budget");
            cache.GetOrAdd("oversized", () => new(new byte[ProcessIconCache.MaximumBytes + 1], "image/png"));
            var oversizeReloaded = false;
            cache.GetOrAdd("oversized", () => { oversizeReloaded = true; return new([], "image/png"); });
            Check(oversizeReloaded, "A single payload exceeding the retention budget is returned but never retained");
        }
        using (var cache = new ProcessIconCache())
        {
            try { cache.GetOrAdd("failed", () => throw new IOException("load failed")); throw new Exception("Failure ignored"); }
            catch (IOException) { }
            Check(cache.Describe() == (0, 0, 0), "Failed loads leave no poisoned lazy, payload or order node");
            Check(cache.GetOrAdd("failed", () => new([1], "image/png")).Data.Length == 1, "A failed key can be loaded again");
            Parallel.For(0, 700, i => cache.GetOrAdd("parallel-" + i, () => new(new byte[32768], "image/png")));
            var stats = cache.Describe();
            Check(stats.Entries <= ProcessIconCache.MaximumEntries && stats.Bytes <= ProcessIconCache.MaximumBytes && stats.Entries == stats.OrderEntries,
                "Concurrent completion and eviction preserve entry, byte and order budgets");
        }
        using (var first = new ProcessIconCache())
        using (var second = new ProcessIconCache())
        {
            var one = first.GetOrAdd("same-path", () => new([1], "image/png"));
            var two = second.GetOrAdd("same-path", () => new([2], "image/png"));
            first.Dispose();
            Check(one.Data[0] == 1 && two.Data[0] == 2 && second.Describe() == (1, 1, 1), "Separate owners never share payload or clear each other's cache");
            Check(first.Describe() == (0, 0, 0), "Disposal clears all retained entries and accounting");
            try { first.GetOrAdd("late", () => throw new Exception("Disposed factory ran")); throw new Exception("Disposed cache accepted work"); }
            catch (ObjectDisposedException) { Check(true, "Disposed owners reject new work before loading"); }
        }
        using (var cache = new ProcessIconCache())
        using (var release = new ManualResetEventSlim())
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var loading = Task.Run(() => cache.GetOrAdd("loading", () =>
            { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return new([3], "image/png"); }));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cache.Dispose(); release.Set();
            Check((await loading.WaitAsync(TimeSpan.FromSeconds(5))).Data[0] == 3 && cache.Describe() == (0, 0, 0),
                "In-flight loading may finish for its caller but never repopulates a disposed owner");
        }
        using (var cache = new ProcessIconCache())
        using (var release = new ManualResetEventSlim())
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var old = Task.Run(() => cache.GetOrAdd("replaced", () =>
            { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return new([4], "image/png"); }));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < ProcessIconCache.MaximumEntries; i++) cache.GetOrAdd("evict-" + i, () => new([], "image/png"));
            var replacement = cache.GetOrAdd("replaced", () => new([5], "image/png"));
            release.Set(); await old.WaitAsync(TimeSpan.FromSeconds(5));
            Check(ReferenceEquals(replacement, cache.GetOrAdd("replaced", () => throw new Exception("Replacement lost"))) && cache.Describe().Bytes == 1,
                "Late completion of an evicted entry cannot overwrite or account against its replacement");
        }
        using (var cache = new ProcessIconCache())
        {
            var payload = LoadThenDispose(cache);
            for (var i = 0; payload.IsAlive && i < 30; i++)
            { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(10); }
            Check(!payload.IsAlive, "Retaining a disposed owner does not retain its former icon bytes");
            GC.KeepAlive(cache);
        }
        Console.WriteLine($"Process icon cache checks passed: {count}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadThenDispose(ProcessIconCache cache)
    {
        var content = cache.GetOrAdd("retained", () => new(new byte[262144], "image/svg+xml"));
        var payload = new WeakReference(content.Data); cache.Dispose(); return payload;
    }
}
