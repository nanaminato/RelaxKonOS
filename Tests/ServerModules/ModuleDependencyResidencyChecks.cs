using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

internal static class ModuleDependencyResidencyChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var builder = WebApplication.CreateBuilder();
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory");
        var logger = services.GetRequiredService<ILogger<ModuleResidencyCoordinator>>();
        static IReadOnlyDictionary<string, IReadOnlyList<string>> Graph(params (string Id, string[] Requires)[] items) =>
            items.ToDictionary(item => item.Id, item => (IReadOnlyList<string>)item.Requires, StringComparer.Ordinal);
        List<string> events = [];
        Factory[] Factories(params string[] ids) => ids.Select(id => new Factory(id, events)).ToArray();
        ModuleResidencyCoordinator Coordinator(Factory[] factories, IReadOnlyDictionary<string, IReadOnlyList<string>> graph,
            IReadOnlySet<string>? pinned = null) => new(factories,
                ModuleResidencyOptions.Parse(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["ModuleResidency:MaxParallelLoads"] = "1", ["ModuleResidency:MaxWaitingTotal"] = "1" }).Build(),
                    factories.Select(item => item.ModuleId).ToHashSet(), factories.Select(item => item.ModuleId).ToHashSet()),
                services, context, logger, dependencies: graph, residentRequiredModuleIds: pinned);
        static async Task Active(ModuleResidencyCoordinator coordinator, string id)
        {
            for (var attempt = 0; attempt < 200; attempt++)
            { if (coordinator.Describe(id)!.State == ModuleResidencyState.Active) return; await Task.Delay(5); }
            throw new Exception("Shared module load did not finish: " + id);
        }
        static async Task Refused(Func<Task<ModuleLease>> action)
        {
            try { using var lease = await action(); throw new Exception("Expected unavailable dependency"); }
            catch (ModuleUnavailableException) { }
        }

        var diamond = Graph(("a", ["b", "c"]), ("b", ["d"]), ("c", ["d"]));
        var nodes = Factories("a", "b", "c", "d");
        await using (var coordinator = Coordinator(nodes, diamond))
        {
            using (var lease = await coordinator.AcquireAsync("a"))
            {
                check(events.SequenceEqual(["load:d", "load:b", "load:c", "load:a"]),
                    "a diamond dependency graph loads in topology order with one initialization slot and one business waiter");
                check(nodes.All(node => node.Created == 1) && coordinator.Describe("d")!.ActiveLeases == 2,
                    "shared dependency generation loads once and holds one lifetime lease per direct consumer");
            }
            check(coordinator.Describe("b")!.ActiveLeases == 1 && coordinator.Describe("c")!.ActiveLeases == 1
                && await coordinator.TrySleepAsync("d", default) == "module-active-work",
                "dependencies remain leased after the business request while the parent instance exists");
            check(await coordinator.TrySleepAsync("a", default) is null
                && coordinator.Describe("b")!.ActiveLeases == 0 && coordinator.Describe("c")!.ActiveLeases == 0,
                "successful parent release returns only its own dependency leases");
            await coordinator.TrySleepAsync("b", default);
            check(coordinator.Describe("d")!.ActiveLeases == 1 && await coordinator.TrySleepAsync("d", default) == "module-active-work",
                "one consumer release cannot release a dependency still used by another consumer");
            await coordinator.TrySleepAsync("c", default); await coordinator.TrySleepAsync("d", default);
            check(events.TakeLast(4).SequenceEqual(["dispose:a", "dispose:b", "dispose:c", "dispose:d"]),
                "dependency resources are released after their consumers");
        }

        events.Clear(); nodes = Factories("a", "b", "c"); nodes[2].FailLoad = true;
        await using (var coordinator = Coordinator(nodes, Graph(("a", ["b", "c"]))))
        {
            await Refused(() => coordinator.AcquireAsync("a"));
            check(nodes[0].Created == 0 && nodes[1].Created == 1 && coordinator.Describe("b")!.ActiveLeases == 0,
                "dependency load failure prevents parent execution and releases earlier dependency acquisitions");
            check(await coordinator.TrySleepAsync("b", default) is null, "a failed dependency chain does not strand an idle loaded sibling");
        }

        events.Clear(); nodes = Factories("a", "b"); nodes[0].FailLoad = true;
        await using (var coordinator = Coordinator(nodes, Graph(("a", ["b"]))))
        {
            await Refused(() => coordinator.AcquireAsync("a"));
            check(nodes[0].Created == 1 && coordinator.Describe("b")!.ActiveLeases == 0
                && await coordinator.TrySleepAsync("b", default) is null,
                "parent factory failure releases all dependency protection after initialization cleanup");
        }

        events.Clear(); nodes = Factories("a", "b"); nodes[1].Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var coordinator = Coordinator(nodes, Graph(("a", ["b"]))))
        {
            using var cancellation = new CancellationTokenSource();
            var waiting = coordinator.AcquireAsync("a", cancellation.Token);
            await nodes[1].Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
            try { using var lease = await waiting; throw new Exception("Cancelled parent acquired a lease"); }
            catch (OperationCanceledException) { }
            nodes[1].Pending!.SetResult(); await Active(coordinator, "a");
            check(nodes.All(node => node.Created == 1) && coordinator.Describe("a")!.ActiveLeases == 0
                && coordinator.Describe("b")!.ActiveLeases == 1,
                "caller cancellation leaves shared dependency initialization alive without granting an abandoned business lease");
            check(await coordinator.TrySleepAsync("a", default) is null && await coordinator.TrySleepAsync("b", default) is null,
                "a shared load abandoned by its caller can release parent and dependency after initialization");
        }

        events.Clear(); nodes = Factories("a", "b"); nodes[0].FailDispose = true;
        await using (var coordinator = Coordinator(nodes, Graph(("a", ["b"]))))
        {
            using (var lease = await coordinator.AcquireAsync("a")) { }
            check(await coordinator.TrySleepAsync("a", default) == "module-recovery-blocked"
                && coordinator.Describe("b")!.ActiveLeases == 1 && await coordinator.TrySleepAsync("b", default) == "module-active-work",
                "failed parent disposal quarantines its executor while retaining the dependency generation");
            await Refused(() => coordinator.AcquireAsync("a"));
            await coordinator.DisposeAsync();
            check(nodes[0].Created == 1 && nodes[1].Last!.Disposals == 0,
                "shutdown does not destroy a dependency beneath a failed consumer executor");
        }

        events.Clear(); nodes = Factories("b");
        await using (var coordinator = Coordinator(nodes, Graph(), new HashSet<string> { "b" }))
        {
            using (var lease = await coordinator.AcquireAsync("b")) { }
            check(coordinator.Describe("b") is { Releasable: false, BlockedBy: "module-resident-dependent" }
                && await coordinator.TrySleepAsync("b", default) == "module-resident-dependent",
                "a dependency required by a resident module is pinned independently of its configured idle policy");
        }

        events.Clear(); nodes = Factories("a", "b", "c", "d");
        await using (var coordinator = Coordinator(nodes, diamond))
        {
            using (var lease = await coordinator.AcquireAsync("a")) { }
            await coordinator.DisposeAsync();
            check(events.TakeLast(4).SequenceEqual(["dispose:a", "dispose:c", "dispose:b", "dispose:d"]),
                "host shutdown tears down module instances in reverse dependency topology");
        }

        events.Clear(); nodes = Factories("a", "b"); nodes[0].Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var coordinator = Coordinator(nodes, Graph(("a", ["b"]))))
        {
            var waiting = coordinator.AcquireAsync("a");
            await nodes[0].Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await coordinator.DisposeAsync(); await Refused(() => waiting);
            check(nodes[0].Last is null && nodes[1].Last!.Disposals == 1,
                "host cancellation finishes pending parent initialization and releases its dependencies before teardown");
        }

        events.Clear(); nodes = Factories("b"); nodes[0].Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var coordinator = Coordinator(nodes, Graph()))
        {
            check(coordinator.TryAcquire("b", TimeSpan.FromMilliseconds(20)) is null,
                "synchronous acquisition returns unavailable at its own bounded deadline");
            nodes[0].Pending!.SetResult(); await Active(coordinator, "b");
            check(coordinator.Describe("b")!.ActiveLeases == 0 && await coordinator.TrySleepAsync("b", default) is null,
                "a timed-out synchronous caller cannot leak a lease after shared loading eventually succeeds");
        }

        foreach (var graph in new[] { Graph(("a", ["b"]), ("b", ["a"])), Graph(("a", ["missing"])), Graph(("missing", ["a"])) })
        {
            nodes = Factories("a", "b");
            try { await using var invalid = Coordinator(nodes, graph); throw new Exception("Invalid runtime graph accepted"); }
            catch (InvalidOperationException) { check(nodes.All(node => node.Created == 0), "invalid or circular runtime dependency graph is rejected before any factory runs"); }
        }
    }

    private sealed class Factory(string id, List<string> events) : IServerModuleRuntimeFactory
    {
        public string ModuleId => id;
        internal int Created;
        internal bool FailLoad;
        internal bool FailDispose;
        internal TaskCompletionSource? Pending;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Runtime? Last;
        public async ValueTask<IServerModuleRuntime> CreateAsync(IServiceProvider services, ServerModuleContext context, CancellationToken cancellationToken)
        {
            Created++; Entered.TrySetResult();
            if (Pending is { } pending) await pending.Task.WaitAsync(cancellationToken);
            if (FailLoad) throw new IOException("injected factory failure");
            events.Add("load:" + id);
            return Last = new Runtime(id, events, FailDispose);
        }
    }
    private sealed class Runtime(string id, List<string> events, bool failDispose) : IServerModuleRuntime
    {
        public string ModuleId => id;
        internal int Disposals;
        public object Resolve(Type serviceType) => throw new NotSupportedException();
        public ValueTask<ModuleUnloadBlocker?> CanUnloadAsync(CancellationToken cancellationToken) => ValueTask.FromResult<ModuleUnloadBlocker?>(null);
        public ValueTask DisposeAsync()
        {
            Disposals++; events.Add("dispose:" + id);
            if (failDispose) throw new IOException("injected consumer disposal failure");
            return ValueTask.CompletedTask;
        }
    }
}
