using System.Diagnostics;
using System.Runtime.CompilerServices;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

internal static class ModuleMemoryRetentionChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var builder = WebApplication.CreateBuilder();
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var ids = new HashSet<string> { "memory-test" };
        await using var coordinator = new ModuleResidencyCoordinator([new Factory()],
            ModuleResidencyOptions.Parse(new ConfigurationBuilder().Build(), ids, ids), services,
            ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"),
            services.GetRequiredService<ILogger<ModuleResidencyCoordinator>>());

        // Keep the completed request task alive, as HTTP callers and task ledgers can do.
        var request = coordinator.AcquireAsync("memory-test");
        await request;
        var runtime = ReleaseRequest(request);
        await coordinator.TrySleepAsync("memory-test", default);
        Collect();
        check(!runtime.IsAlive, "a completed load and retained disposed request lease do not root a sleeping runtime");
        GC.KeepAlive(request);

        var owned = CaptureContainer(services);
        await owned.Container.DisposeAsync();
        Collect();
        check(!owned.Capture.IsAlive, "a disposed container drops factory closures even while the container is retained");
        GC.KeepAlive(owned.Container);

        var stream = await ClosedStreamAsync(coordinator, asynchronous: false);
        Collect();
        check(!stream.Inner.IsAlive, "a retained closed stream wrapper releases its inner stream and buffer");
        GC.KeepAlive(stream.Wrapper);
        var asyncStream = await ClosedStreamAsync(coordinator, asynchronous: true);
        Collect();
        check(!asyncStream.Inner.IsAlive, "async stream cleanup also drops the inner stream reference");
        GC.KeepAlive(asyncStream.Wrapper);

        List<WeakReference> generations = [];
        Collect();
        var before = GC.GetTotalMemory(false);
        var started = Stopwatch.GetTimestamp();
        for (var cycle = 0; cycle < 24; cycle++)
        {
            var acquisition = coordinator.AcquireAsync("memory-test");
            await acquisition;
            generations.Add(ReleaseRequest(acquisition));
            check(await coordinator.TrySleepAsync("memory-test", default) is null, $"memory cycle {cycle + 1} releases safely");
        }
        Collect();
        check(generations.All(reference => !reference.IsAlive), "all 24 sleeping runtime generations and their 2 MiB payloads are collectible");
        Console.WriteLine($"MEMORY synthetic cycles=24 liveGenerations={generations.Count(reference => reference.IsAlive)} " +
            $"managedBefore={before} managedAfter={GC.GetTotalMemory(false)} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}");
        GC.KeepAlive(coordinator);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ReleaseRequest(Task<ModuleLease> request)
    {
        var lease = request.GetAwaiter().GetResult();
        var reference = new WeakReference(lease.Runtime);
        lease.Dispose();
        lease.Dispose(); // duplicate completion must remain harmless
        return reference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ModuleServiceContainer Container, WeakReference Capture) CaptureContainer(IServiceProvider root)
    {
        var payload = new byte[2 * 1024 * 1024];
        var container = new ModuleServiceContainer("memory-test", root);
        container.AddSingleton<byte[]>(_ => payload);
        return (container, new WeakReference(payload));
    }

    private static async Task<(Stream Wrapper, WeakReference Inner)> ClosedStreamAsync(ModuleResidencyCoordinator coordinator, bool asynchronous)
    {
        var lease = await coordinator.AcquireAsync("memory-test");
        var wrapper = Wrap(lease);
        if (asynchronous) await wrapper.Wrapper.DisposeAsync();
        else wrapper.Wrapper.Dispose();
        await coordinator.TrySleepAsync("memory-test", default);
        return wrapper;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Stream Wrapper, WeakReference Inner) Wrap(ModuleLease lease)
    {
        var inner = new MemoryStream(new byte[2 * 1024 * 1024]);
        return (new ModuleLeasedStream(inner, lease), new WeakReference(inner));
    }

    private static void Collect()
    {
        // Test instrumentation only: production release must never force a process-wide GC.
        for (var attempt = 0; attempt < 3; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); }
    }

    private sealed class Factory : IServerModuleRuntimeFactory
    {
        public string ModuleId => "memory-test";
        public ValueTask<IServerModuleRuntime> CreateAsync(IServiceProvider services, ServerModuleContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult<IServerModuleRuntime>(new Runtime());
    }

    private sealed class Runtime : IServerModuleRuntime
    {
        private readonly byte[] _payload = new byte[2 * 1024 * 1024];
        public string ModuleId => "memory-test";
        public object Resolve(Type serviceType) => _payload;
        public ValueTask<ModuleUnloadBlocker?> CanUnloadAsync(CancellationToken cancellationToken) => ValueTask.FromResult<ModuleUnloadBlocker?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
