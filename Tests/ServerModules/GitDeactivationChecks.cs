using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Server.Git;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

internal static class GitDeactivationChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", "git-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "builds"));
        var file = Path.Combine(root, "builds", "builds.json");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["ModuleManagement:Authority"] = "managed", ["GitBuilds:RootDirectory"] = "builds" });
        var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory");
        using var store = new ModuleManagementStore(builder.Configuration, root);
        var factory = new GateFactory();
        var services = new ServiceCollection().AddLogging().AddSingleton(store);
        services.AddSingleton(sp => new ModuleResidencyCoordinator([factory], ModuleResidencyOptions.Parse(builder.Configuration,
            new HashSet<string> { ServerModules.GitId }, new HashSet<string> { ServerModules.GitId }), sp, context, sp.GetRequiredService<ILogger<ModuleResidencyCoordinator>>()));
        await using var provider = services.BuildServiceProvider();
        var coordinator = provider.GetRequiredService<ModuleResidencyCoordinator>();
        factory.Coordinator = coordinator;
        var all = store.DesiredIds;
        var disabled = ServerModules.CreateManaged(all.Where(id => id != ServerModules.GitId).ToHashSet());
        var enabled = ServerModules.CreateManaged(new HashSet<string> { ServerModules.GitId });
        var operation = new GitBuildOperationDto(Guid.NewGuid(), "https://github.com/example/project.git", "refs/heads/main", new string('a', 40),
            ".", "Dockerfile", "queued", null, null, null, DateTimeOffset.UtcNow, null, []);
        byte[] Ledger(GitBuildOperationDto value) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            credentials = Array.Empty<object>(),
            builds = new[] { new { owner = Guid.NewGuid(), keyHash = new string('a', 64), requestHash = new string('b', 64), operation = value } },
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        async Task<bool> StartupRefused()
        {
            try { await disabled.ValidateDeactivationAsync(provider, context, default); return false; }
            catch (InvalidOperationException error) when (error.Message.Contains(ServerModules.GitId)) { return true; }
        }
        try
        {
            await disabled.ValidateDeactivationAsync(provider, context, default);
            check(!File.Exists(file) && factory.Created == 0, "missing Git build ledger does not create state or load a module during deactivation inspection");
            foreach (var state in new[] { "queued", "running", "interrupted", "failed", "cancelled", "succeeded" })
            {
                var value = operation with { State = state, FinishedAt = state is "queued" or "running" ? null : DateTimeOffset.UtcNow };
                var bytes = Ledger(value); File.WriteAllBytes(file, bytes);
                var facts = await enabled.DescribeAsync(provider, context, default);
                var git = facts.Single(module => module.Id == ServerModules.GitId);
                var blocks = state is "queued" or "running" or "interrupted";
                check((git.OrphanedWork.Count > 0) == blocks && await StartupRefused() == blocks
                    && git.RecoveryState == (blocks ? "required" : "none")
                    && git.AllowedActions!.Single(action => action.Action == ServerModuleAction.Wake).Allowed == !blocks
                    && bytes.SequenceEqual(File.ReadAllBytes(file)) && factory.Created == 0,
                    "Git read-only inventory and disabled startup agree without rewriting or loading state " + state);
            }
            var cleanupFailed = Ledger(operation with { State = "failed", FinishedAt = DateTimeOffset.UtcNow, ProblemCode = "git-build.process-cleanup-failed" });
            File.WriteAllBytes(file, cleanupFailed);
            check(await StartupRefused(), "a terminal-looking Git record with unconfirmed process cleanup still blocks disabled startup");
            foreach (var malformed in new[] { "invalid", "{}", "{\"credentials\":[],\"builds\":[],\"builds\":[]}",
                "{\"credentials\":[],\"builds\":[],\"password\":\"not-a-real-secret\"}",
                System.Text.Encoding.UTF8.GetString(Ledger(operation with { State = "mystery" })) })
            {
                File.WriteAllText(file, malformed);
                var result = GitBuildService.InspectLedger(root, new GitBuildOptions { RootDirectory = "builds" });
                check(await StartupRefused() && result.Count == 1 && !result[0].Description.Contains("not-a-real-secret")
                    && File.ReadAllText(file) == malformed && factory.Created == 0,
                    "unreadable or invalid Git ledger blocks deactivation without exposing content or rewriting it");
            }
            using (var oversized = new FileStream(file, FileMode.Create)) oversized.SetLength(32L * 1024 * 1024 + 1);
            check(await StartupRefused(), "oversized Git build ledger is rejected before unbounded parsing");
            File.Delete(file); Directory.CreateDirectory(file);
            check(await StartupRefused(), "a directory occupying the Git ledger path is unknown state, not an empty ledger");
            Directory.Delete(file);
            var done = Ledger(operation with { State = "cancelled", FinishedAt = DateTimeOffset.UtcNow }); File.WriteAllBytes(file, done);
            using (var business = await coordinator.AcquireAsync(ServerModules.GitId))
            using (var cleanup = factory.Last!.Source.Acquire())
            {
                var facts = await enabled.DescribeAsync(provider, context, default);
                var request = new ServerModulePlanRequest(store.ServiceId, ServerModuleAction.Disable, [ServerModules.GitId],
                    all.Where(id => id != ServerModules.GitId).ToArray(), store.Revision, "git-active-cleanup");
                var plan = store.Preview("actor", request, facts);
                check(plan.BlockedBy.Any(blocker => blocker.ProblemCode == "module-recovery-blocked") && done.SequenceEqual(File.ReadAllBytes(file)),
                    "a cancelled Git record cannot permit a disable plan while its current-generation cleanup lease remains active");
            }
            var quiet = await enabled.DescribeAsync(provider, context, default);
            check(quiet.Single(module => module.Id == ServerModules.GitId).OrphanedWork.Count == 0 && factory.Created == 1,
                "completed Git cleanup removes the live-work blocker without a new runtime generation");
            var target = new ServerModulePlanRequest(store.ServiceId, ServerModuleAction.Disable, [ServerModules.GitId],
                all.Where(id => id != ServerModules.GitId).ToArray(), store.Revision, "git-quiet");
            var allowed = store.Preview("actor", target, quiet);
            File.WriteAllBytes(file, Ledger(operation));
            var changed = await enabled.DescribeAsync(provider, context, default);
            try { store.Apply("actor", allowed.PlanId, changed); throw new Exception("Git work appeared but old disable applied"); }
            catch (ModuleManagementException error) when (error.Code == "module-recovery-blocked")
            { check(true, "Git disable apply rechecks durable work that appeared after preview"); }
        }
        finally { await coordinator.DisposeAsync(); store.Dispose(); Directory.Delete(root, true); }
    }
    private sealed class GateFactory : IServerModuleRuntimeFactory
    {
        public string ModuleId => ServerModules.GitId;
        internal ModuleResidencyCoordinator Coordinator = null!; internal int Created; internal Runtime? Last;
        public ValueTask<IServerModuleRuntime> CreateAsync(IServiceProvider services, ServerModuleContext context, CancellationToken ct)
        { Created++; return ValueTask.FromResult<IServerModuleRuntime>(Last = new Runtime(Coordinator)); }
    }
    private sealed class Runtime : IServerModuleRuntime
    {
        internal readonly IModuleWorkLeaseSource Source;
        internal Runtime(ModuleResidencyCoordinator coordinator) => Source = new ModuleWorkLeaseSource(coordinator, this);
        public string ModuleId => ServerModules.GitId;
        public object Resolve(Type type) => throw new NotSupportedException();
        public ValueTask<ModuleUnloadBlocker?> CanUnloadAsync(CancellationToken ct) => ValueTask.FromResult<ModuleUnloadBlocker?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
