using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Hubs;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Settings;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.Storage;
using RelaxKonOS.Server.Terminal;
using RoyalTerminal.Terminal;

internal static class TerminalResidencyChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value, string text) { if (!value) throw new Exception(text); Console.WriteLine($"PASS TERMINAL RESIDENCY {++count}: {text}"); }
        await using (var f = new Fixture())
        {
            var first = await f.CreateAsync(); var session = first.Session; var pty = f.Factory.Last!;
            Check(first.Created && f.Active == 1 && pty.Starts == 1, "Actual manager starts one PTY under a real continuous work lease");
            await session.AttachAsync("first", () => { }); session.Detach("first");
            var restored = await f.CreateAsync(session.SessionId);
            Check(!restored.Created && ReferenceEquals(restored.Session, session) && f.Active == 1 && pty.Stops == 0, "Detach and reconnect preserve the same protected PTY instead of terminating it");
            pty.Emit([1, 2, 3]);
            await session.AttachAsync("second", () => { });
            Check(f.Client.Payloads.Single().SequenceEqual(new byte[] { 1, 2, 3 }), "Reconnect replays detached output through the tracked sender");
            await f.Manager.RemoveAsync(session.SessionId);
            Check(f.Active == 0 && pty.Stops == 1 && pty.Subscribers == 0 && !f.Manager.TryGet(session.SessionId, out _), "Confirmed close drains sends, unsubscribes and removes its owner before releasing protection");
        }
        await using (var f = new Fixture())
        {
            var session = (await f.CreateAsync()).Session; var pty = f.Factory.Last!;
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); f.Client.Block = pending;
            var aborts = 0; await session.AttachAsync("slow", () => aborts++);
            pty.Emit([1]); await UntilAsync(() => f.Client.Sends == 1);
            pty.Emit(new byte[TerminalSession.MaximumQueuedBytes]);
            Check(!session.IsAttached && aborts == 1 && pty.IsRunning && f.Active == 1, "Slow-consumer overflow detaches its connection while retaining the live PTY and ring history");
            var close = f.Manager.RemoveAsync(session.SessionId);
            await UntilAsync(() => pty.Stops == 1);
            Check(!close.IsCompleted && f.Active == 1, "Close cannot detach an unfinished typed output send after PTY cleanup");
            using var budget = new CancellationTokenSource(); var stop = f.Manager.StopAsync(budget.Token); budget.Cancel();
            try { await stop; } catch (OperationCanceledException) { }
            Check(!close.IsCompleted && f.Active == 1, "Host stop timeout preserves the real output task and session lease");
            pending.TrySetResult(); await close.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Manager.StopAsync(default);
            Check(f.Active == 0 && f.Client.Sends == 1, "Actual output completion precedes zero work without replaying overflowed live output");
        }
        await using (var f = new Fixture())
        {
            var session = (await f.CreateAsync()).Session; var pty = f.Factory.Last!;
            pty.StopGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var drain = f.Coordinator.DisposeAsync().AsTask(); await UntilAsync(() => pty.Stops == 1);
            Check(!drain.IsCompleted && f.Active == 1, "Coordinator stopping waits for actual PTY cleanup rather than only requesting stop");
            pty.StopGate.TrySetResult(); await drain.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Active == 0 && !f.Manager.TryGet(session.SessionId, out _), "Confirmed backend cleanup ends the protected session and coordinator drain");
            try { await f.CreateAsync(); throw new Exception("Closed coordinator started PTY"); } catch (ModuleUnavailableException) { }
            Check(f.Factory.Calls == 1, "Closed coordinator cannot be bypassed to create a new PTY");
        }
        await using (var f = new Fixture())
        {
            f.Factory.FailStart = true;
            try { await f.CreateAsync(); throw new Exception("Failed PTY start accepted"); } catch (IOException) { }
            Check(f.Active == 0 && f.Manager.ListForUser(f.User.ToString()).Count == 0, "Startup failure performs confirmed cleanup before returning the lease and removing the entry");
            await f.Manager.StopAsync(default);
            try { await f.CreateAsync(); throw new Exception("Stopped manager started PTY"); } catch (InvalidOperationException) { }
            Check(f.Factory.Calls == 1, "Stopped owner refuses new starts before factory access");
        }
        var leases = new TerminalTestLeases(); var faultPty = new Pty { StopFailure = new IOException("test cleanup") };
        var fault = new TerminalSession("fault", "user", faultPty, new Hub(new Client()), _ => throw new Exception("False clean"), await leases.AcquireAsync(ServerModules.TerminalId));
        fault.FinishStart();
        try { await fault.CloseAsync(); throw new Exception("Unknown cleanup released"); } catch (IOException) { }
        try { await fault.CloseAsync(); throw new Exception("Unknown cleanup retried"); } catch (IOException) { }
        Check(leases.Active == 1 && leases.RecoveryRequired == 1 && faultPty.Stops == 1, "Unknown backend cleanup isolates its runtime and retains fault responsibility and lease without replay");
        if (OperatingSystem.IsWindows())
        {
            using var pty = new ConPty();
            pty.Start("powershell", 80, 24, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), null, ["-NoProfile", "-NonInteractive", "-Command", "Write-Output terminal-lifetime"]);
            await pty.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(!pty.IsRunning, "Own native ConPTY root and owned threads complete asynchronous stop");
        }
        if (OperatingSystem.IsLinux() && geteuid() != 0)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            using var pty = new LocalUserTerminalPty(new DefaultPtyFactory().Create(), new(Guid.NewGuid(),
                new(RelaxKonOS.Protocol.Common.HostPlatformKind.Linux, geteuid().ToString(System.Globalization.CultureInfo.InvariantCulture), Environment.UserName, home)));
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pty.DataReceived += (_, _) => { entered.TrySetResult(); release.Wait(); };
            pty.Start("/bin/sh", 80, 24, home, null, ["-c", "printf terminal-lifetime; read value"]);
            Task? cleanup = null;
            var waitedForReader = false;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                cleanup = pty.StopAsync();
                await UntilAsync(() => !pty.IsRunning);
                waitedForReader = !cleanup.IsCompleted;
            }
            finally { release.Set(); }
            if (cleanup is not null) await cleanup.WaitAsync(TimeSpan.FromSeconds(15));
            Check(waitedForReader && !pty.IsRunning, "Own native Unix PTY waits for its real reader, then confirms and reaps the bound root through pidfd");
        }
        Console.WriteLine($"Terminal residency checks passed: {count}");
    }
    private static async Task UntilAsync(Func<bool> done)
    { var until = DateTime.UtcNow.AddSeconds(5); while (!done()) { if (DateTime.UtcNow > until) throw new TimeoutException(); await Task.Delay(10); } }
    [DllImport("libc")] private static extern uint geteuid();
    private sealed class Pty : IPty, IWorkspaceTerminalPty, ITerminalPtyLifetime
    {
        internal int Starts, Stops;
        internal bool FailStart;
        internal Exception? StopFailure;
        internal TaskCompletionSource? StopGate;
        private Action<byte[], int>? _data;
        private Action<int>? _exit;
        internal int Subscribers => (_data?.GetInvocationList().Length ?? 0) + (_exit?.GetInvocationList().Length ?? 0);
        public bool IsRunning { get; private set; }
        public int ChildPid { get; set; }
        public RelaxKonOS.Protocol.Settings.TerminalEnvironmentOverrides? WorkspaceEnvironment { get; set; }
        public string DefaultWorkingDirectory => Environment.CurrentDirectory;
        public event Action<byte[], int>? DataReceived { add => _data += value; remove => _data -= value; }
        public event Action<int>? ProcessExited { add => _exit += value; remove => _exit -= value; }
        internal void Emit(byte[] data) => _data?.Invoke(data, data.Length);
        public void Start(string? shell, int columns, int rows, string? directory, Dictionary<string, string>? environment, IReadOnlyList<string>? arguments)
        { Starts++; if (FailStart) throw new IOException("test start"); IsRunning = true; }
        public void Write(byte[] data, int offset, int count) { }
        public void Write(string text) { }
        public void Resize(int columns, int rows) { }
        public void Resize(int columns, int rows, int width, int height) { }
        public void Stop() { IsRunning = false; }
        public void Dispose() => Stop();
        public async Task StopAsync() { Stops++; if (StopFailure is { } error) throw error; if (StopGate is { } gate) await gate.Task; Stop(); _exit?.Invoke(0); }
    }
    private sealed class Factory : IPtyFactory
    {
        internal int Calls; internal bool FailStart; internal Pty? Last;
        public IPty Create() { Calls++; return Last = new Pty { FailStart = FailStart }; }
    }
    private sealed class Client : ITerminalHubClient
    {
        internal int Sends; internal TaskCompletionSource? Block; internal readonly List<byte[]> Payloads = new();
        public Task OnOutput(byte[] bytes) { lock (Payloads) Payloads.Add(bytes); Interlocked.Increment(ref Sends); return Block?.Task ?? Task.CompletedTask; }
        public Task OnProcessExited(int code) => Task.CompletedTask;
    }
    private sealed class Hub(Client client) : IHubContext<TerminalHub, ITerminalHubClient>
    { public IHubClients<ITerminalHubClient> Clients { get; } = new HubClients(client); public IGroupManager Groups => throw new NotSupportedException(); }
    private sealed class HubClients(Client client) : IHubClients<ITerminalHubClient>
    {
        public ITerminalHubClient All => client;
        public ITerminalHubClient AllExcept(IReadOnlyList<string> ids) => client;
        public ITerminalHubClient Client(string id) => client;
        public ITerminalHubClient Clients(IReadOnlyList<string> ids) => client;
        public ITerminalHubClient Group(string id) => client;
        public ITerminalHubClient GroupExcept(string id, IReadOnlyList<string> ids) => client;
        public ITerminalHubClient Groups(IReadOnlyList<string> ids) => client;
        public ITerminalHubClient User(string id) => client;
        public ITerminalHubClient Users(IReadOnlyList<string> ids) => client;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "rk-terminal-lifetime-" + Guid.NewGuid().ToString("N"));
        internal readonly Guid User = Guid.NewGuid();
        internal readonly Factory Factory = new(); internal readonly Client Client = new();
        internal ModuleResidencyCoordinator Coordinator => _app.Services.GetRequiredService<ModuleResidencyCoordinator>();
        internal TerminalSessionManager Manager => _app.Services.GetRequiredService<TerminalSessionManager>();
        internal int Active => Coordinator.Describe(ServerModules.TerminalId)?.ActiveLeases ?? 0;
        internal Fixture()
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            var modules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.TerminalId });
            modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
            var workspaces = new InMemoryWorkspaceRepository(); workspaces.Add(new Workspace { Id = Guid.NewGuid(), UserId = User });
            builder.Services.AddScoped<IWorkspaceRepository>(_ => workspaces);
            builder.Services.AddSingleton(new WorkspaceEnvironmentService(_directory, DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_directory, "keys")))));
            builder.Services.AddSingleton<IPtyFactory>(Factory); builder.Services.AddSingleton<IHubContext<TerminalHub, ITerminalHubClient>>(new Hub(Client));
            _app = builder.Build();
        }
        internal Task<(TerminalSession Session, bool Created)> CreateAsync(string? id = null) => Manager.GetOrCreateAsync(User.ToString(), id, new(80, 24, 0, 0, null, null));
        public async ValueTask DisposeAsync()
        {
            Factory.Last?.StopGate?.TrySetResult(); Client.Block?.TrySetResult();
            await Manager.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); await _app.DisposeAsync();
            var full = Path.GetFullPath(_directory); var prefix = Path.GetFullPath(Path.GetTempPath());
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new Exception("Invalid test path");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
