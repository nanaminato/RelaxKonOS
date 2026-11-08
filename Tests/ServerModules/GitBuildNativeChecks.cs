using Microsoft.AspNetCore.DataProtection;
using System.Runtime.Loader;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Server.Git;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

internal static class GitBuildNativeChecks
{
    internal static async Task RunAsync(string mode, string root, string branch, string commit)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(Path.Combine(root, "private-engine")))
            throw new InvalidOperationException("The private Linux Git build fixture is required.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var ct = deadline.Token;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GitBuilds:RootDirectory"] = "builds",
            ["GitBuilds:AllowedHosts:0"] = "repo.mb03.test",
            ["GitBuilds:BuilderName"] = "mb03-live",
            ["GitBuilds:BuilderContainer"] = "mb03-builder",
            ["GitBuilds:BuilderNetwork"] = "mb03-git",
        });
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        var modules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.GitId });
        modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
        await using var app = builder.Build();
        var git = app.Services.GetRequiredService<IGitBuildService>();
        var residency = app.Services.GetRequiredService<ModuleResidencyCoordinator>();
        var factory = (GitModuleRuntimeFactory)modules.RuntimeFactories.Single();
        var owner = Guid.Parse(File.ReadAllText(Path.Combine(root, "owner")));
        var body = new GitBuildRequest("https://repo.mb03.test/mb03/source.git", $"refs/heads/{branch}", commit, ".", "Dockerfile");
        var key = $"native-build-{branch}";
        var ledger = Path.Combine(root, "builds", "builds.json");

        if (mode == "recover")
        {
            var operation = await git.StartAsync(owner, body, key, ct);
            var id = operation.Id;
            Require(id == Guid.Parse(File.ReadAllText(Path.Combine(root, "crash-id")))
                && operation.State == "interrupted" && operation.ProblemCode == "git-build.interrupted"
                && git.Get(owner, id)?.FinishedAt == operation.FinishedAt, "original durable interruption");
            try
            {
                await git.StartAsync(owner, body, "different-recovery-key", ct);
                throw new InvalidOperationException("The unresolved build admitted another executor.");
            }
            catch (GitBuildProblem error) when (error.Code == "git-build.recovery-required") { }
            Require(await residency.TrySleepAsync(ServerModules.GitId, ct) == "module-release-blocked", "uncertain dispatch blocks release");
            Console.WriteLine($"PASS GIT BUILDKIT CRASH RECOVERY: {id}; original receipt and dispatch gate");
            return;
        }

        var (terminal, code) = await ExecuteAndReleaseAsync(git, residency, factory, mode, root, ledger, owner, body, key, ct);
        Require(AssemblyLoadContext.GetLoadContext(terminal.Logs.GetType().Assembly)?.IsCollectible == false,
            "the original receipt holds Host-owned log storage");
        Require(await CollectAsync(code, ct), "the real build ALC is collected while the original receipt remains reachable");
        var replay = await git.StartAsync(owner, body, key, ct);
        Require(replay.Id == terminal.Id && replay.FinishedAt == terminal.FinishedAt && replay.State == terminal.State
            && factory.LastCodeReference != code, "a fresh generation reopens the original receipt");
        Console.WriteLine($"PASS GIT BUILDKIT {mode.ToUpperInvariant()}: {terminal.Id}; actual ALC collected; original receipt reopened");
    }

    private static async Task<(GitBuildOperationDto Terminal, WeakReference Code)> ExecuteAndReleaseAsync(IGitBuildService git,
        ModuleResidencyCoordinator residency, GitModuleRuntimeFactory factory, string mode, string root, string ledger,
        Guid owner, GitBuildRequest body, string key, CancellationToken ct)
    {
        var operation = await git.StartAsync(owner, body, key, ct);
        var id = operation.Id;
        var image = $"relaxkonos-git:build-{id:N}";
        if (mode is "cancel" or "crash")
        {
            await WaitAsync(() => File.Exists(Path.Combine(root, "fetch-entered")));
            Require(GitBuildLedger.ReadLedger(ledger).Builds.Single(entry => entry.Operation.Id == id).DispatchStarted == true
                && residency.HasBackgroundWork(ServerModules.GitId)
                && await residency.TrySleepAsync(ServerModules.GitId, ct) == "module-active-work", "real HTTPS fetch holds the original generation");
            if (mode == "crash")
            {
                File.WriteAllText(Path.Combine(root, "crash-id"), id.ToString());
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            git.Cancel(owner, id);
            await WaitAsync(() => File.Exists(Path.Combine(root, "cleanup-entered")));
            Require(git.Get(owner, id)?.FinishedAt is null && residency.HasBackgroundWork(ServerModules.GitId)
                && await residency.TrySleepAsync(ServerModules.GitId, ct) == "module-active-work", "image cleanup retains the original work");
            File.WriteAllText(Path.Combine(root, "release-cleanup"), "");
        }

        await WaitAsync(() => !residency.HasBackgroundWork(ServerModules.GitId));
        var terminal = git.Get(owner, id)!;
        Require(terminal.FinishedAt is not null && (mode == "success"
            ? terminal.State == "succeeded" && terminal.ImageReference == image && terminal.ImageId is not null
            : terminal.State == "cancelled" && terminal.ProblemCode == "git-build.cancelled" && terminal.ImageReference is null),
            $"durable {mode} receipt ({terminal.State}/{terminal.ProblemCode}, {string.Join(" | ", terminal.Logs.TakeLast(6))})");
        var code = factory.LastCodeReference!;
        Require(await residency.TrySleepAsync(ServerModules.GitId, ct) is null, "completed real build releases its generation");
        return (terminal, code);

        async Task WaitAsync(Func<bool> condition)
        {
            while (!condition()) await Task.Delay(50, ct);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task<bool> CollectAsync(WeakReference code, CancellationToken ct)
    {
        // Leave the completion continuation before observing collection of the released generation.
        for (var attempt = 0; attempt < 80; attempt++)
        {
            await Task.Delay(100, ct);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (!code.IsAlive) return true;
        }
        return false;
    }
}
