using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Server.Git;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

internal static class ModuleBackgroundWorkChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await using (var fixture = new Fixture())
        {
            var request = await fixture.Coordinator.AcquireAsync(ServerModules.GitId);
            var oldSource = fixture.Factory.Last!.Source;
            var work = oldSource.Acquire(); request.Dispose();
            check(fixture.Leases == 1 && await fixture.Coordinator.TrySleepAsync(ServerModules.GitId, default) == "module-active-work",
                "accepted background work protects its exact module generation after the request lease ends");
            work.Dispose(); await fixture.Coordinator.TrySleepAsync(ServerModules.GitId, default);
            using (var next = await fixture.Coordinator.AcquireAsync(ServerModules.GitId))
            {
                try { using var wrong = oldSource.Acquire(); throw new Exception("Old generation started work"); }
                catch (ModuleUnavailableException error) when (error.Code == "module-runtime-generation-mismatch")
                { check(true, "an old module instance cannot acquire work protection for a replacement generation"); }
                work = fixture.Factory.Last!.Source.Acquire();
            }
            using var host = new CancellationTokenSource(); fixture.Coordinator.Start(host.Token);
            var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishing = Task.Run(async () =>
            {
                try { await Task.Delay(Timeout.Infinite, work.Stopping); } catch (OperationCanceledException) { }
                await cleanup.Task; work.Dispose();
            });
            host.Cancel(); var stopping = fixture.Coordinator.DisposeAsync().AsTask();
            check(work.Stopping.IsCancellationRequested && !stopping.IsCompleted && fixture.Factory.Last!.Disposals == 0,
                "host stop cancels background work and waits for its cleanup without disposing its runtime underneath it");
            cleanup.SetResult(); await finishing; await stopping;
            check(fixture.Factory.Last!.Disposals == 1, "runtime teardown follows completed background cleanup");
        }
        await VerifyProcessCancellationAsync(check);
        await VerifyUnreadableLedgerAsync(check);
        if (!OperatingSystem.IsLinux()) return;
        var owner = Guid.NewGuid();
        var requestBody = new GitBuildRequest("https://github.com/example/project.git", "refs/heads/main", new string('a', 40), ".", "Dockerfile");
        await using (var fixture = new Fixture())
        {
            using var http = new CancellationTokenSource();
            var operation = await fixture.Proxy.StartAsync(owner, requestBody, "work-request-one", http.Token);
            await fixture.Runner.BuildEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); http.Cancel();
            check(fixture.Leases == 1 && !fixture.Runner.BuildToken.IsCancellationRequested,
                "accepted Git build owns a background lease and does not inherit later HTTP cancellation");
            var replay = await fixture.Proxy.StartAsync(owner, requestBody, "work-request-one", default);
            check(replay.Id == operation.Id && fixture.Runner.Builds == 1 && fixture.Leases == 1,
                "Git build idempotency returns the original record without creating another worker or work lease");
            fixture.Proxy.Cancel(owner, operation.Id);
            await fixture.Runner.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(fixture.Leases == 1 && await fixture.Coordinator.TrySleepAsync(ServerModules.GitId, default) == "module-active-work",
                "Git build cancellation retains protection through its operation-owned image cleanup");
            fixture.Runner.Cleanup.TrySetResult(); await fixture.Idle();
            check(fixture.Proxy.Get(owner, operation.Id)?.State == "cancelled" && fixture.Runner.Cleanups == 1,
                "Git build cleanup persists the cancellation result before ending background ownership");
            await fixture.Coordinator.TrySleepAsync(ServerModules.GitId, default);
            replay = await fixture.Proxy.StartAsync(owner, requestBody, "work-request-one", default);
            check(replay.State == "cancelled" && fixture.Runner.Builds == 1 && fixture.Leases == 0,
                "Git build replay after runtime release reads its original ledger instead of rerunning the build");
        }
        await using (var fixture = new Fixture())
        {
            fixture.Runner.Cleanup.TrySetResult();
            await fixture.Proxy.StartAsync(owner, requestBody, "work-host-stop", default);
            await fixture.Runner.BuildEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Coordinator.DisposeAsync();
            var ledger = JsonNode.Parse(File.ReadAllText(fixture.Ledger))!;
            check(ledger["builds"]![0]!["operation"]!["state"]!.GetValue<string>() == "interrupted"
                && fixture.Factory.Last!.Disposals == 1,
                "host shutdown drains an actual Git build worker and records interruption rather than a user cancellation");
        }
        await using (var fixture = new Fixture())
        {
            fixture.Runner.Cleanup.TrySetResult();
            await fixture.Proxy.StartAsync(owner, requestBody, "work-ledger-failure", default);
            await fixture.Runner.BuildEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.CreateDirectory(fixture.Ledger + ".tmp");
            fixture.Runner.Build.TrySetResult(); await fixture.Idle();
            check(await fixture.Coordinator.TrySleepAsync(ServerModules.GitId, default) == "module-recovery-blocked"
                && fixture.Factory.Created == 1 && fixture.Factory.Last!.Service.RequiresRecovery,
                "Git build terminal persistence failure quarantines its original runtime instead of silently dropping recovery responsibility");
        }
    }

    private static async Task VerifyUnreadableLedgerAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", "git-bad-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "builds")); File.WriteAllText(Path.Combine(root, "builds", "builds.json"), "invalid");
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root }); builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["GitBuilds:RootDirectory"] = "builds" });
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            var modules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.GitId });
            modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
            await using var app = builder.Build();
            try { app.Services.GetRequiredService<IGitBuildService>().Credentials(Guid.NewGuid()); throw new Exception("Invalid build ledger accepted"); }
            catch (GitBuildProblem error) when (error.Code == "git-build.store-unavailable") { }
            var coordinator = app.Services.GetRequiredService<ModuleResidencyCoordinator>();
            check(await coordinator.TrySleepAsync(ServerModules.GitId, default) == "module-release-blocked"
                && coordinator.Describe(ServerModules.GitId)!.BlockedBy == "module-recovery-blocked",
                "the actual Git runtime refuses memory release when its build ledger cannot be read");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task VerifyProcessCancellationAsync(Action<bool, string> check)
    {
        var marker = Path.Combine(AppContext.BaseDirectory, "test-data", "git-child-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        try
        {
            var arguments = new List<string>();
            var executable = Environment.ProcessPath!;
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                arguments.Add(Assembly.GetExecutingAssembly().Location);
            arguments.AddRange(["--git-build-command-child", marker]);
            using var cancellation = new CancellationTokenSource();
            var running = new GitBuildCommandRunner().RunAsync(executable, arguments, new Dictionary<string, string>(), TimeSpan.FromSeconds(20), cancellation.Token);
            var pid = 0;
            for (var attempt = 0; attempt < 500 && pid == 0; attempt++)
            {
                try { if (File.Exists(marker)) int.TryParse(File.ReadAllText(marker), out pid); } catch (IOException) { }
                if (pid == 0) await Task.Delay(10);
            }
            if (pid == 0) throw new Exception("Git command fixture failed to start");
            cancellation.Cancel();
            try { await running; throw new Exception("Cancelled Git process completed normally"); } catch (OperationCanceledException) { }
            var exited = false;
            try { using var child = Process.GetProcessById(pid); exited = child.HasExited; } catch (ArgumentException) { exited = true; }
            check(exited, "Git command cancellation waits for the real child process to exit before returning");
        }
        finally { File.Delete(marker); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services = new ServiceCollection().AddLogging().BuildServiceProvider();
        private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-data", "git-work-" + Guid.NewGuid().ToString("N"));
        internal readonly Runner Runner = new(); internal readonly WorkFactory Factory;
        internal ModuleResidencyCoordinator Coordinator { get; }
        internal IGitBuildService Proxy { get; }
        internal int Leases => Coordinator.Describe(ServerModules.GitId)!.ActiveLeases;
        internal string Ledger => Path.Combine(_root, "builds", "builds.json");
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = _root });
            Factory = new WorkFactory(builder.Environment, Runner);
            var ids = new HashSet<string> { ServerModules.GitId };
            Coordinator = new([Factory], ModuleResidencyOptions.Parse(builder.Configuration, ids, ids), _services,
                ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"), _services.GetRequiredService<ILogger<ModuleResidencyCoordinator>>());
            Factory.Coordinator = Coordinator;
            Proxy = ModuleServiceProxy<IGitBuildService>.Create(Coordinator, ServerModules.GitId);
        }
        internal async Task Idle()
        {
            for (var attempt = 0; attempt < 500; attempt++) { if (Leases == 0) return; await Task.Delay(10); }
            throw new Exception("Git worker did not finish cleanup");
        }
        public async ValueTask DisposeAsync()
        {
            Runner.Build.TrySetResult(); Runner.Cleanup.TrySetResult();
            await Coordinator.DisposeAsync(); await _services.DisposeAsync(); Directory.Delete(_root, true);
        }
    }
    private sealed class WorkFactory(IHostEnvironment environment, Runner runner) : IServerModuleRuntimeFactory
    {
        internal ModuleResidencyCoordinator Coordinator = null!; internal Runtime? Last; internal int Created;
        private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();
        public string ModuleId => ServerModules.GitId;
        public ValueTask<IServerModuleRuntime> CreateAsync(IServiceProvider services, ServerModuleContext context, CancellationToken ct)
        { Created++; return ValueTask.FromResult<IServerModuleRuntime>(Last = new Runtime(Coordinator, environment, runner, _protection, services)); }
    }
    private sealed class Runtime : IServerModuleRuntime
    {
        internal readonly IModuleWorkLeaseSource Source; internal readonly GitBuildService Service; internal int Disposals;
        internal Runtime(ModuleResidencyCoordinator coordinator, IHostEnvironment environment, Runner runner, IDataProtectionProvider protection, IServiceProvider services)
        {
            Source = new ModuleWorkLeaseSource(coordinator, this);
            Service = new(environment, protection, new GitBuildOptions { RootDirectory = "builds", BuilderName = "test-builder", BuilderContainer = "test-container", BuilderNetwork = "test-network" },
                Source, runner, services.GetRequiredService<ILogger<GitBuildService>>());
        }
        public string ModuleId => ServerModules.GitId;
        public object Resolve(Type type) => Service;
        public ValueTask<ModuleUnloadBlocker?> CanUnloadAsync(CancellationToken ct) => ValueTask.FromResult<ModuleUnloadBlocker?>(null);
        public ValueTask DisposeAsync() { Disposals++; Service.Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class Runner : IGitBuildCommandRunner
    {
        internal readonly TaskCompletionSource Build = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource BuildEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource CleanupEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken BuildToken; internal int Builds; internal int Cleanups;
        public async Task<(int ExitCode, List<string> Lines)> RunAsync(string file, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environment,
            TimeSpan timeout, CancellationToken ct, Action<IReadOnlyList<string>>? onOutput = null)
        {
            if (file == "git") return (0, [new string('a', 40) + "\trefs/heads/main"]);
            if (args.Take(2).SequenceEqual(["buildx", "build"]))
            { Builds++; BuildToken = ct; BuildEntered.TrySetResult(); await Build.Task.WaitAsync(ct); return (0, []); }
            if (args.Take(2).SequenceEqual(["image", "rm"]))
            { Cleanups++; CleanupEntered.TrySetResult(); await Cleanup.Task; return (0, []); }
            if (args.Take(2).SequenceEqual(["image", "inspect"])) return (0, ["sha256:" + new string('b', 64)]);
            if (args.Take(2).SequenceEqual(["buildx", "inspect"])) return (0, ["Driver: remote", "docker-container://test-container"]);
            if (args.Contains("{{.Config.User}}")) return (0, ["1000"]);
            if (args.Contains("{{.State.Running}}")) return (0, ["true"]);
            return (0, ["{\"Privileged\":false,\"Memory\":1048576,\"PidsLimit\":64,\"NanoCpus\":1000000000,\"NetworkMode\":\"test-network\",\"Binds\":[],\"CapAdd\":[]}"]);
        }
    }
}
