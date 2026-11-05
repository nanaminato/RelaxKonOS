using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Server.Git;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Hubs;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.Storage.Sqlite;
using RelaxKonOS.Server.Terminal;
using RelaxKonOS.Server.UserExecution;
using RoyalTerminal.Terminal;

// A production-service workload fixture, explicitly NOT the complete production Server host.
// It never invokes GC, trims working sets, or changes host firewall/tailnet configuration.
internal static class ModuleBaselineWorkload
{
    internal static async Task RunAsync(string root, string order)
    {
        if (order is not ("git-first" or "terminal-first")) throw new ArgumentException("Unknown workload order");
        root = Path.GetFullPath(root);
        if (Directory.Exists(root)) throw new IOException("A fresh workload directory is required");
        Directory.CreateDirectory(root);
        var watch = Stopwatch.StartNew();
        var owner = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ModuleResidency:IdleGraceSeconds"] = "300" });
        builder.Services.AddDbContextFactory<RelaxKonOSDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(root, "fixture.sqlite")};Pooling=False"));
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddSingleton<IGitRepositoryStorage, HostGitRepositoryStorage>();
        builder.Services.AddSingleton<IGitRepositoryHost, HostGitRepositoryAccess>();
        builder.Services.AddSingleton<IServerModeResolver, UserMode>();
        builder.Services.AddSingleton(new UserExecutionBackendSelection(UserExecutionBackend.Helper));
        builder.Services.AddSingleton(DispatchProxy.Create<IUserExecutionTransport, RejectTransport>());
        builder.Services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", owner.ToString())], "fixture")) } });
        var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "sqlite");
        var factory = new GitModuleRuntimeFactory();
        var ids = new HashSet<string> { ServerModules.GitId, ServerModules.TerminalId };
        builder.Services.AddSingleton(services => new ModuleResidencyCoordinator([factory, new ResidentModuleWorkRuntimeFactory(ServerModules.TerminalId)],
            ModuleResidencyOptions.Parse(builder.Configuration, ids, new HashSet<string> { ServerModules.GitId }), services, context,
            services.GetRequiredService<ILogger<ModuleResidencyCoordinator>>(), residentWorkModuleIds: new HashSet<string> { ServerModules.TerminalId }));
        await using var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        await using (var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<RelaxKonOSDbContext>>().CreateDbContextAsync())
            await db.Database.EnsureCreatedAsync();
        var coordinator = app.Services.GetRequiredService<ModuleResidencyCoordinator>();
        var proxy = ModuleServiceProxy<IGitRepositoryService>.Create(coordinator, ServerModules.GitId);
        await Point("ready", new { startup_ms = watch.Elapsed.TotalMilliseconds, scope = "partial-production-service-fixture", connections = 0, sessions = 0 });
        await Point("idle", new { modules = "Git dormant; Terminal resident work identity" });
        if (order == "terminal-first") await Terminal();
        var repository = Path.Combine(root, "worktree"); Directory.CreateDirectory(repository);
        var business = Stopwatch.StartNew();
        if (!(await proxy.InitRepositoryAsync(repository, owner)).Success) throw new IOException("Git init failed");
        var id = Guid.Parse((await proxy.RegisterRepositoryAsync(new("baseline", repository), owner)).Id);
        await File.WriteAllTextAsync(Path.Combine(repository, "tracked.txt"), "MB-01 deterministic fixture\n");
        var staged = await proxy.StageAsync(id, owner, new(["tracked.txt"]));
        if (!staged.Success) throw new IOException("Git stage failed: " + staged.Message);
        VerifyStatus(await proxy.GetStatusAsync(id, owner));
        var registered = await proxy.ListRepositoriesAsync(owner);
        if (registered.Count != 1 || registered[0].Id != id.ToString() || registered[0].Path != repository)
            throw new IOException("Git registration read did not return the expected sole repository");
        var console = await proxy.GetConsoleAsync(id, owner);
        if (!console.Any(entry => entry.Command == "git --literal-pathspecs add -- tracked.txt" && entry.ExitCode == 0))
            throw new IOException("Git console did not confirm the successful staging command");
        await Point("git-business", new { elapsed_ms = business.Elapsed.TotalMilliseconds, state = coordinator.Describe(ServerModules.GitId)!.State.ToString(), staged_path_confirmed = true, console_exit_confirmed = true });
        var reason = await coordinator.TrySleepAsync(ServerModules.GitId, default);
        if (reason is not null || coordinator.Describe(ServerModules.GitId)!.State != ModuleResidencyState.Dormant) throw new InvalidOperationException("Git release blocked: " + reason);
        await Point("git-released", new { state = "Dormant", code_weak_reference_alive = factory.LastCodeReference!.IsAlive, gc_forced = false });
        business.Restart();
        VerifyStatus(await proxy.GetStatusAsync(id, owner));
        await Point("git-rewake", new { elapsed_ms = business.Elapsed.TotalMilliseconds, generation = coordinator.Describe(ServerModules.GitId)!.Generation });
        if (order == "git-first") await Terminal();
        await Point("firewall-missing", new { reason = "No authenticated real Helper transport in this fixture; no simulated firewall result counted as business" });
        await Point("complete", new { full_production_host_baseline = false });

        static void VerifyStatus(RelaxKonOS.Protocol.Git.GitStatusDto status)
        {
            if (status.Staged.Count != 1 || status.Staged[0].Path != "tracked.txt"
                || status.Unstaged.Count != 0 || status.Untracked.Count != 0 || status.Conflicts.Count != 0)
                throw new IOException("Git status did not confirm the expected staged file");
        }

        async Task Point(string phase, object details)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { phase, utc = DateTimeOffset.UtcNow, pid = Environment.ProcessId, order, details }));
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        async Task Terminal()
        {
            var lease = await new ResidentModuleWorkLeaseSource(coordinator).AcquireAsync(ServerModules.TerminalId);
            IPty pty = OperatingSystem.IsWindows() ? new ConPty() : new LocalUserTerminalPty(new DefaultPtyFactory().Create(), new(owner,
                new(HostPlatformKind.Linux, geteuid().ToString(System.Globalization.CultureInfo.InvariantCulture), Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))));
            var session = new TerminalSession("baseline", owner.ToString(), pty, new NullHub(), _ => { }, lease);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var outputGate = new object();
            var tail = "";
            void Observe(byte[] bytes, int count)
            {
                lock (outputGate)
                {
                    // ASCII marker may cross PTY frames; retain only a bounded suffix.
                    tail += Encoding.ASCII.GetString(bytes, 0, count);
                    if (tail.Contains("MB01_READY", StringComparison.Ordinal)) ready.TrySetResult();
                    if (tail.Length > 128) tail = tail[^128..];
                }
            }
            void Exited(int code) => ready.TrySetException(new IOException("PTY exited before readiness marker: " + code));
            pty.DataReceived += Observe;
            pty.ProcessExited += Exited;
            try
            {
                pty.Start(OperatingSystem.IsWindows() ? "powershell" : "/bin/sh", 80, 24, root, null,
                    OperatingSystem.IsWindows() ? ["-NoProfile", "-NonInteractive", "-Command", "Write-Output MB01_READY; Start-Sleep 600"] : ["-c", "printf 'MB01_READY\\n'; sleep 600"]);
                session.FinishStart();
                // A live transport process alone does not prove shell readiness or output delivery.
                // Expiry is a failed run and never emits terminal-live.
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                if (!pty.IsRunning) throw new IOException("PTY not running");
                await Point("terminal-live", new { transport_owner_pid = pty.ChildPid, sessions = 1, connections = 0, marker_observed = true, work_protected = coordinator.HasBackgroundWork(ServerModules.TerminalId) });
                await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(30));
                if (coordinator.HasBackgroundWork(ServerModules.TerminalId)) throw new IOException("Terminal responsibility remains after close");
                await Point("terminal-closed", new { sessions = 0, close_confirmed = true });
            }
            finally
            {
                session.FinishStart();
                await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(30));
                pty.DataReceived -= Observe;
                pty.ProcessExited -= Exited;
                (pty as IDisposable)?.Dispose();
            }
        }
    }
    [DllImport("libc")] private static extern uint geteuid();
    private sealed class UserMode : IServerModeResolver
    { public ServerMode Mode => ServerMode.User; public bool Supports(ServerHostFeature feature) => false; public ServerCapabilitiesDto Describe() => throw new NotSupportedException(); }
    public class RejectTransport : DispatchProxy
    { protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new InvalidOperationException("Fixture must not invoke Helper"); }
    private sealed class NullHub : IHubContext<TerminalHub, ITerminalHubClient>
    { public IHubClients<ITerminalHubClient> Clients { get; } = DispatchProxy.Create<IHubClients<ITerminalHubClient>, NullClients>(); public IGroupManager Groups => throw new NotSupportedException(); }
    public class NullClients : DispatchProxy
    { protected override object? Invoke(MethodInfo? method, object?[]? args) => new Client(); }
    private sealed class Client : ITerminalHubClient
    { public Task OnOutput(byte[] bytes) => Task.CompletedTask; public Task OnProcessExited(int code) => Task.CompletedTask; }
}
