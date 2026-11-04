using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.Startup.Modules;

internal static class ProxyOperationResponsibilityChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine($"PASS PROXY RESPONSIBILITY {++count}: {label}"); }
        var root = Path.Combine(Path.GetTempPath(), "proxy-responsibility-" + Guid.NewGuid().ToString("N"));
        var paths = new TestProxyPaths(root);
        var file = Path.Combine(paths.GetStateDirectory(), "proxy-operations.json");
        var services = new ServiceCollection(); services.AddSingleton<IProxyPlatformPaths>(paths);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider()); services.AddSingleton<MihomoPublicationStore>();
        using var provider = services.BuildServiceProvider();
        var guard = new ProxyModule();
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "sqlite");
        async Task<bool> Blocked() => (await guard.InspectOrphanedWorkAsync(provider, context, default)).Count != 0;
        try
        {
            Check(!await Blocked() && !Directory.Exists(paths.GetStateDirectory()), "Disabled-host inspection of missing ledgers is read-only and does not create state");
            Directory.CreateDirectory(paths.GetStateDirectory());
            foreach (var state in Enum.GetValues<ProxyOperationState>())
            {
                var operation = new ProxyOperationDto(Guid.NewGuid(), "subscription.refresh", state, "stage", "", null, null);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { new { IdempotencyKey = "key", Operation = operation } });
                await File.WriteAllBytesAsync(file, bytes);
                Check(await Blocked() == (state is ProxyOperationState.Queued or ProxyOperationState.Running or ProxyOperationState.Interrupted),
                    "Persistent state has the correct deactivation responsibility: " + state);
                Check((await File.ReadAllBytesAsync(file)).SequenceEqual(bytes), "Inspection does not normalize or rewrite the durable state: " + state);
            }
            var unknown = new ProxyOperationDto(Guid.NewGuid(), "subscription.activate", ProxyOperationState.Failed, "failed", ProxyProblemCodes.OperationInterrupted, null, DateTimeOffset.UtcNow);
            var wire = JsonSerializer.Serialize(new[] { new { IdempotencyKey = "unknown", Operation = unknown } });
            await File.WriteAllTextAsync(file, wire);
            Check(await Blocked(), "Failed execution with unknown interruption responsibility cannot be treated as a clean failure");
            foreach (var invalid in new[] { "", "null", "{", "[{}]", wire.Replace("\"unknown\"", "\"unknown\",\"IdempotencyKey\":\"other\""),
                wire.Replace("\"IdempotencyKey\":", "\"Unknown\":0,\"IdempotencyKey\":"), wire[..^1] + "," + wire[1..] })
            {
                await File.WriteAllTextAsync(file, invalid);
                Check(await Blocked(), "Malformed, duplicate or unknown ledger content blocks deactivation");
                var rejected = false;
                var store = new ProxyOperationStore(paths, NullLogger<ProxyOperationStore>.Instance, new TestProxyWorkLeases());
                try { await store.EnqueueAsync("new", "test", (_, _) => throw new Exception("Corrupt ledger executed work"), default); }
                catch (Exception error) when (error is InvalidDataException or JsonException) { rejected = true; }
                Check(rejected && await File.ReadAllTextAsync(file) == invalid, "Admission cannot replace unknown history with a fresh successful ledger");
            }
            File.Delete(file); Directory.CreateDirectory(file);
            Check(await Blocked(), "Directory replacement is unknown, not an empty task ledger"); Directory.Delete(file);
            if (!OperatingSystem.IsWindows())
            {
                var target = Path.Combine(root, "other.json"); await File.WriteAllTextAsync(target, "[]"); File.CreateSymbolicLink(file, target);
                Check(await Blocked(), "A linked operation ledger is rejected without following it"); File.Delete(file);
            }
            var live = new ProxyOperationStore(paths, NullLogger<ProxyOperationStore>.Instance, new TestProxyWorkLeases());
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var operationTask = await live.EnqueueAsync("active", "subscription.refresh", async (_, _) => { started.TrySetResult(); await release.Task; return null; }, default);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                Check((await live.InspectResponsibilitiesAsync(default)).Count != 0, "The live task owner reports active execution under the admission gate");
                Check(await Blocked(), "Disabled-host inspection also sees the persisted running operation");
            }
            finally { release.TrySetResult(); }
            await live.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            Check((await live.InspectResponsibilitiesAsync(default)).Count == 0 && !await Blocked(),
                "Completed cleanup and confirmed success leave no task responsibility; stopping alone is not a blocker");
            var cached = new ProxyOperationStore(paths, NullLogger<ProxyOperationStore>.Instance, new TestProxyWorkLeases());
            await cached.GetAsync(operationTask.OperationId, default);
            await File.WriteAllTextAsync(file, "{");
            Check((await cached.InspectResponsibilitiesAsync(default)).Count != 0, "A loaded in-memory cache cannot hide later disk corruption from inspection");
            var refused = false;
            try { await cached.EnqueueAsync("corrupt-live", "test", (_, _) => throw new Exception("Unknown history executed work"), default); }
            catch (JsonException) { refused = true; }
            Check(refused && await File.ReadAllTextAsync(file) == "{", "Live admission also refuses corrupted disk state without replacing it");
            await File.WriteAllTextAsync(file, wire);
            var reopened = new ProxyOperationStore(paths, NullLogger<ProxyOperationStore>.Instance, new TestProxyWorkLeases());
            refused = false;
            try { await reopened.EnqueueAsync("after-interruption", "test", (_, _) => Task.FromResult<string?>(null), default); }
            catch (ProxyOperationValidationException) { refused = true; }
            Check(refused, "Reopening unknown execution responsibility blocks new admission while permitting inspection");
            using (var output = File.Create(file)) output.SetLength(32 * 1024 * 1024 + 1);
            Check(await Blocked(), "Oversized history is unknown rather than silently truncated");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            refused = false;
            try { await guard.InspectOrphanedWorkAsync(provider, context, cancelled.Token); }
            catch (OperationCanceledException) { refused = true; }
            Check(refused, "Cancelled inspection propagates cancellation instead of reporting safe deactivation");
            await File.WriteAllTextAsync(file, "[]");
            foreach (var name in new[] { "proxy-tun-recovery.json", "mihomo-settings.json", "proxy-server.protected", "squid-configuration.json" })
            {
                var duty = Path.Combine(paths.GetStateDirectory(), name);
                await File.WriteAllTextAsync(duty, "{");
                Check(await Blocked(), "Unreadable persistent safety responsibility blocks deactivation without constructing management: " + name);
                File.Delete(duty);
            }
            var inboundStore = new RelaxKonOS.Server.ProxyServer.ProxyServerStore(paths, provider.GetRequiredService<IDataProtectionProvider>());
            await inboundStore.WriteAsync(RelaxKonOS.Server.ProxyServer.ProxyServerState.Empty with
                { PendingPublication = new(new(1, []), [], []) }, default);
            Check(await Blocked(), "Unconfirmed inbound publication remains visible to the combined deactivation gate");
            File.Delete(Path.Combine(paths.GetStateDirectory(), "proxy-server.protected"));
            await File.WriteAllTextAsync(Path.Combine(paths.GetStateDirectory(), "squid-configuration.json"),
                JsonSerializer.Serialize(new RelaxKonOS.Protocol.GatewayProxy.SquidConfigurationState(null, 1, null, null, "squid.publication.unconfirmed")));
            Check(await Blocked(), "Unconfirmed Squid intent cannot be discarded by disabling management");
            File.Delete(Path.Combine(paths.GetStateDirectory(), "squid-configuration.json"));
            var historyId = await SeedHistoryAsync(file);
            var retainedStore = new ProxyOperationStore(paths, NullLogger<ProxyOperationStore>.Instance, new TestProxyWorkLeases());
            var historical = await ReadWeakHistoryAsync(retainedStore, historyId);
            var retainedRead = retainedStore.GetAsync(Guid.Empty, default);
            Check(await retainedRead is null, "A missing historical operation remains missing");
            for (var pass = 0; pass < 10 && historical.IsAlive; pass++)
            { await Task.Delay(20); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Check(!historical.IsAlive,
                "Root ledger and a retained completed lookup do not retain previously returned history objects");
            GC.KeepAlive(retainedStore); GC.KeepAlive(retainedRead);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"Proxy task responsibility checks passed: {count}");
    }

    private static async Task<Guid> SeedHistoryAsync(string file)
    {
        var id = Guid.NewGuid(); var text = new string('m', 8192);
        var history = Enumerable.Range(0, 1024).Select(index => new
        {
            IdempotencyKey = "history-" + index,
            Operation = new ProxyOperationDto(index == 0 ? id : Guid.NewGuid(), "subscription.refresh", ProxyOperationState.Succeeded,
                text, "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        }).ToArray();
        await File.WriteAllBytesAsync(file, JsonSerializer.SerializeToUtf8Bytes(history)); return id;
    }
    private static async Task<WeakReference> ReadWeakHistoryAsync(ProxyOperationStore store, Guid id)
    {
        var operation = await store.GetAsync(id, default) ?? throw new Exception("Missing seeded history");
        return new WeakReference(operation);
    }
}
