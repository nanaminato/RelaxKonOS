using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Server.Hubs;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.Terminal;
using RelaxKonOS.Server.UserExecution;
using RelaxKonOS.Server.Privileged;
using RoyalTerminal.Terminal;

internal static class TerminalTreeChecks
{
    internal static async Task RunAsync(UserExecutionContext? helperContext = null, string? helperPath = null)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (OperatingSystem.IsLinux() && (helperContext is null ? geteuid() == 0 : geteuid() != 0)) throw new InvalidOperationException("Ordinary tree checks require an ordinary driver; Helper checks require the root test driver.");
        if (helperContext is not null && (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(helperPath) || !Path.IsPathFullyQualified(helperPath)))
            throw new InvalidOperationException("An explicit current Helper path is required.");
        var root = Path.Combine(Path.GetTempPath(), "rk-terminal-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        if (helperContext is not null) AssignOwner(root, helperContext);
        var count = 0;
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine($"PASS TERMINAL TREE {++count}: {text}"); }
        try
        {
            foreach (var expectedCode in new int?[] { null, 0, 7 })
            {
                var natural = expectedCode.HasValue;
                var mode = natural ? "natural" + expectedCode : "wait";
                var directory = Path.Combine(root, natural ? "root-first-" + expectedCode : "explicit-close"); Directory.CreateDirectory(directory);
                WriteScripts(directory);
                if (helperContext is not null) AssignOwner(directory, helperContext);
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory }); builder.Logging.ClearProviders();
                var modules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.TerminalId });
                modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
                await using var app = builder.Build();
                var coordinator = app.Services.GetRequiredService<ModuleResidencyCoordinator>();
                var lease = await app.Services.GetRequiredService<IResidentModuleWorkLeaseSource>().AcquireAsync(ServerModules.TerminalId, default);
                using var sentinel = new TerminalProcessProbe(StartSentinel(helperContext));
                IPty pty = helperContext is not null ? new LinuxUserTerminalPty(helperContext, new PrivilegedHelperOptions { HelperPath = helperPath!, SudoPath = "/usr/bin/sudo" })
                    : OperatingSystem.IsWindows() ? new ConPty() : new LocalUserTerminalPty(new DefaultPtyFactory().Create(), new(Guid.NewGuid(),
                    new(HostPlatformKind.Linux, geteuid().ToString(System.Globalization.CultureInfo.InvariantCulture), Environment.UserName,
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))));
                var session = new TerminalSession(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), pty, new NullHub(), _ => { }, lease);
                var handles = new List<TerminalProcessProbe>();
                var actualExit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                pty.ProcessExited += code => actualExit.TrySetResult(code);
                try
                {
                    var shell = OperatingSystem.IsWindows() ? "powershell" : "/bin/sh";
                    IReadOnlyList<string> arguments = OperatingSystem.IsWindows()
                        ? ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(directory, "root.ps1"), directory, mode]
                        : [Path.Combine(directory, "root.sh"), directory, mode];
                    pty.Start(shell, 80, 24, directory, null, helperContext is null ? arguments : null); session.FinishStart();
                    if (helperContext is not null)
                        pty.Write("exec /bin/sh " + Quote(Path.Combine(directory, "root.sh")) + " " + Quote(directory) + " " + (mode) + "\n");
                    await UntilAsync(() => File.Exists(Path.Combine(directory, "ready")) && new[] { "root", "child", "leaf" }.All(name => ReadPid(Path.Combine(directory, name)) > 0));
                    foreach (var name in new[] { "root", "child", "leaf" })
                    {
                        var pid = int.Parse((await File.ReadAllTextAsync(Path.Combine(directory, name))).Trim());
                        handles.Add(new TerminalProcessProbe(Process.GetProcessById(pid)));
                    }
                    if (OperatingSystem.IsLinux())
                    {
                        PrintMemory("transport-owner", pty.ChildPid);
                        foreach (var process in handles) PrintMemory("fixture-descendant", process.Id);
                        // A sudo transport may have a separate actual Helper child. This is a
                        // point sample, not a summed tree peak or a pre/post comparison.
                        var children = File.ReadAllText($"/proc/{pty.ChildPid}/task/{pty.ChildPid}/children");
                        foreach (var child in children.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                            PrintMemory("transport-child", int.Parse(child));
                    }
                    Check(handles.All(process => !process.HasExited) && coordinator.HasBackgroundWork(ServerModules.TerminalId),
                        $"{(natural ? "root-first" : "close")}: real root, child and detached grandchild are alive under a real resident lease");
                    session.Detach("not-attached");
                    Check(handles.All(process => !process.HasExited) && !session.Completion.IsCompleted,
                        "Detach leaves the actual terminal tree and its responsibility alive");
                    if (natural) await File.WriteAllTextAsync(Path.Combine(directory, "release"), "exit");
                    else _ = session.CloseAsync();
                    await session.Completion.WaitAsync(TimeSpan.FromSeconds(30));
                    var observed = handles.Select(process => $"pid={process.Id},kernel-exited={process.HasExited},cached-net-exited={process.CachedHasExited},reaped={process.IsReaped},proc-present={Directory.Exists($"/proc/{process.Id}")},stat={ReadStat(process.Id)}").ToArray();
                    Console.WriteLine("TERMINAL TREE COMPLETION background=" + coordinator.HasBackgroundWork(ServerModules.TerminalId)
                        + " active=" + coordinator.Describe(ServerModules.TerminalId)?.ActiveLeases + " processes=[" + string.Join("; ", observed) + "]");
                    Check(handles.All(process => process.HasExited && process.IsReaped) && !coordinator.HasBackgroundWork(ServerModules.TerminalId),
                        (natural ? "Root-first natural exit drains detached descendants before releasing the real lease" : "Explicit close confirms the entire detached tree before releasing the real lease")
                        + "; background=" + coordinator.HasBackgroundWork(ServerModules.TerminalId) + "; " + string.Join("; ", observed));
                    if (natural) Check(await actualExit.Task.WaitAsync(TimeSpan.FromSeconds(2)) == expectedCode,
                        $"Natural root exit preserves its real status {expectedCode} after complete tree cleanup");
                    Check(!sentinel.HasExited, "Unrelated process with the same identity survives terminal tree cleanup");
                    await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2));
                    Check(handles.All(process => process.HasExited && process.IsReaped), "Repeated session close observes the completed cleanup without a second execution");
                }
                finally
                {
                    // Cleanup only exact test-created handles, never a UID-wide process set.
                    try { await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                    foreach (var process in handles) { try { if (!process.HasExited) process.Kill(); } catch { } process.Dispose(); }
                    try { await sentinel.CleanSentinelAsync(); } catch { }
                    (pty as IDisposable)?.Dispose();
                }
            }
            Console.WriteLine($"Terminal {(helperContext is null ? "local" : "current Helper drop-UID")} tree checks passed: {count}");
        }
        finally
        {
            var full = Path.GetFullPath(root); var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new Exception("Invalid test root");
            Directory.Delete(full, true);
        }
    }
    private static void PrintMemory(string role, int pid)
    {
        try
        {
            var rss = File.ReadLines($"/proc/{pid}/status").FirstOrDefault(line => line.StartsWith("VmRSS:", StringComparison.Ordinal))?.Trim();
            string? pss = null;
            try { pss = File.ReadLines($"/proc/{pid}/smaps_rollup").FirstOrDefault(line => line.StartsWith("Pss:", StringComparison.Ordinal))?.Trim(); }
            catch (UnauthorizedAccessException) { }
            Console.WriteLine($"TERMINAL MEMORY POINT role={role} pid={pid} {rss ?? "VmRSS unavailable"} {pss ?? "Pss unavailable"}");
        }
        catch (IOException) { Console.WriteLine($"TERMINAL MEMORY POINT role={role} pid={pid} unavailable"); }
    }
    private static Process StartSentinel(UserExecutionContext? helperContext)
    {
        var info = new ProcessStartInfo(helperContext is not null ? "/usr/bin/sudo" : OperatingSystem.IsWindows() ? "powershell" : "/bin/sleep") { UseShellExecute = false, CreateNoWindow = true };
        if (OperatingSystem.IsWindows()) foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep 600" }) info.ArgumentList.Add(argument);
        else
        {
            if (helperContext is not null) foreach (var argument in new[] { "-n", "-u", "#" + helperContext.Identity.StableIdentity, "/bin/sleep" }) info.ArgumentList.Add(argument);
            info.ArgumentList.Add("600");
        }
        return Process.Start(info)!;
    }
    private static async Task UntilAsync(Func<bool> done)
    { var deadline = DateTime.UtcNow.AddSeconds(20); while (!done()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Terminal tree fixture did not become ready."); await Task.Delay(20); } }
    private static void WriteScripts(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(directory, "root.ps1"), """
                param($dir,$mode)
                Set-Content -Path (Join-Path $dir root) -Value $PID
                $child = Start-Process powershell -WindowStyle Hidden -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',('"' + (Join-Path $dir child.ps1) + '"'),('"' + $dir + '"')) -PassThru
                while (!(Test-Path (Join-Path $dir leaf))) { Start-Sleep -Milliseconds 20 }
                Set-Content -Path (Join-Path $dir ready) -Value ready
                if ($mode -like 'natural*') { while (!(Test-Path (Join-Path $dir release))) { Start-Sleep -Milliseconds 20 }; exit ([int]$mode.Substring(7)) }
                Start-Sleep 600
                """, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "child.ps1"), """
                param($dir)
                Set-Content -Path (Join-Path $dir child) -Value $PID
                $leaf = Start-Process powershell -WindowStyle Hidden -ArgumentList @('-NoProfile','-NonInteractive','-Command','Start-Sleep 600') -PassThru
                Set-Content -Path (Join-Path $dir leaf) -Value $leaf.Id
                Start-Sleep 600
                """, new UTF8Encoding(false));
        }
        else
        {
            File.WriteAllText(Path.Combine(directory, "root.sh"), """
                dir=$1; mode=$2
                echo $$ > "$dir/root"
                setsid /bin/sh "$dir/child.sh" "$dir" &
                while [ ! -f "$dir/leaf" ]; do sleep .02; done
                echo ready > "$dir/ready"
                if [ "$mode" != wait ]; then while [ ! -f "$dir/release" ]; do sleep .02; done; exit "${mode#natural}"; fi
                read value
                """.Replace("\r", ""));
            File.WriteAllText(Path.Combine(directory, "child.sh"), """
                dir=$1
                trap '' HUP
                echo $$ > "$dir/child"
                setsid /bin/sh "$dir/leaf.sh" "$dir" &
                wait
                """.Replace("\r", ""));
            File.WriteAllText(Path.Combine(directory, "leaf.sh"), """
                dir=$1
                trap '' HUP
                echo $$ > "$dir/leaf"
                exec sleep 600
                """.Replace("\r", ""));
        }
    }
    private static string ReadStat(int pid)
    {
        if (!OperatingSystem.IsLinux()) return "not-linux";
        try { return File.ReadAllText($"/proc/{pid}/stat").Trim(); }
        catch (IOException error) { return error.GetType().Name; }
    }
    private static int ReadPid(string path)
    {
        try { return int.TryParse(File.ReadAllText(path).Trim(), out var pid) ? pid : 0; }
        catch (IOException) { return 0; }
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    private static void AssignOwner(string directory, UserExecutionContext context)
    {
        if (chown(directory, uint.Parse(context.Identity.StableIdentity), uint.MaxValue) != 0) throw new IOException("Unable to assign the exact test directory owner.");
    }
    [DllImport("libc")] private static extern int chown(string path, uint user, uint group);
    [DllImport("libc")] private static extern uint geteuid();
    private sealed class NullHub : IHubContext<TerminalHub, ITerminalHubClient>
    { public IHubClients<ITerminalHubClient> Clients { get; } = new HubClients(); public IGroupManager Groups => throw new NotSupportedException(); }
    private sealed class HubClients : IHubClients<ITerminalHubClient>
    {
        private readonly Client _client = new();
        public ITerminalHubClient All => _client;
        public ITerminalHubClient AllExcept(IReadOnlyList<string> ids) => _client;
        public ITerminalHubClient Client(string id) => _client;
        public ITerminalHubClient Clients(IReadOnlyList<string> ids) => _client;
        public ITerminalHubClient Group(string id) => _client;
        public ITerminalHubClient GroupExcept(string id, IReadOnlyList<string> ids) => _client;
        public ITerminalHubClient Groups(IReadOnlyList<string> ids) => _client;
        public ITerminalHubClient User(string id) => _client;
        public ITerminalHubClient Users(IReadOnlyList<string> ids) => _client;
    }
    private sealed class Client : ITerminalHubClient
    { public Task OnOutput(byte[] bytes) => Task.CompletedTask; public Task OnProcessExited(int code) => Task.CompletedTask; }
}
