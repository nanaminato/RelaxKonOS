using System.Security.Principal;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Protocol.Settings;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Hubs;
using RelaxKonOS.Server.Settings;
using RelaxKonOS.Server.Storage;
using RelaxKonOS.Server.Terminal;
using RelaxKonOS.Server.UserExecution;
using RoyalTerminal.Terminal;

internal static class WorkspaceTerminalChecks
{
    public static async Task RunAsync(string root)
    {
        var directory = Path.Combine(root, "workspace-terminals");
        var environment = new WorkspaceEnvironmentService(directory, DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "keys"))));
        var workspaces = new InMemoryWorkspaceRepository();
        var workspace = workspaces.Add(new Workspace { Id = Guid.NewGuid(), UserId = Guid.NewGuid() });
        environment.Save(workspace, new("0", new([new("TERMINAL_VALUE", EnvironmentMutationKind.Set, "first")]), EnvironmentPathMode.Append));
        using var services = new ServiceCollection().AddLogging().AddSignalR().Services.BuildServiceProvider();
        var factory = new CapturingFactory();
        var manager = new TerminalSessionManager(factory, services.GetRequiredService<IHubContext<TerminalHub, ITerminalHubClient>>(), environment, workspaces);
        var request = new StartTerminalRequest(80, 24, 0, 0, null, null);
        var first = manager.GetOrCreate(workspace.UserId.ToString("D"), null, request);
        Check(first.Created && factory.Last!.WorkspaceEnvironment!.Values.Single().Value == "first", "New terminals read Workspace overrides.");
        environment.Save(workspace, new("1", new([new("TERMINAL_VALUE", EnvironmentMutationKind.Set, "second")]), EnvironmentPathMode.Append));
        var restored = manager.GetOrCreate(workspace.UserId.ToString("D"), first.Session.SessionId, request);
        Check(!restored.Created && factory.Calls == 1 && factory.Last!.WorkspaceEnvironment!.Values.Single().Value == "first", "Restoring an existing terminal does not restart or replace its environment.");
        var second = manager.GetOrCreate(workspace.UserId.ToString("D"), null, request);
        Check(second.Created && factory.Last!.WorkspaceEnvironment!.Values.Single().Value == "second", "A new terminal uses the latest saved revision.");
        manager.Remove(first.Session.SessionId); manager.Remove(second.Session.SessionId);

        var identity = new UserExecutionIdentity(HostPlatformKind.Windows, "test", "test", "C:/test");
        var overrides = new TerminalEnvironmentOverrides([new("TEST", EnvironmentMutationKind.Set, "data")], EnvironmentPathMode.Append);
        var wire = new UserExecutionRequest(identity, UserExecutionOperationKind.TerminalStart, Path: "C:/test", TerminalColumns: 80, TerminalRows: 24,
            TerminalWidthPixels: 0, TerminalHeightPixels: 0, OperationId: Guid.NewGuid(), TerminalEnvironment: overrides);
        Check(UserExecutionRequestPolicy.IsValid(wire, true), "Ordinary terminal request accepts bounded overrides.");
        Check(!UserExecutionRequestPolicy.IsValid(wire with { TerminalAdministrator = true }, true), "Administrator terminal refuses Workspace environment payload.");
        Check(!UserExecutionRequestPolicy.IsValid(wire with { Version = "1.6" }, true), "Old user execution protocol is rejected.");
        Check(!UserExecutionRequestPolicy.IsValid(wire with { Operation = UserExecutionOperationKind.FileReadText }, false), "Nonterminal requests reject environment payloads.");
        Check(overrides.IsValid(false) && overrides.Apply(new Dictionary<string, string> { ["PATH"] = "/bin" }, false)["TEST"] == "data", "Validated overrides remain literal workload data.");
        Check(!new TerminalEnvironmentOverrides([new("TEST", EnvironmentMutationKind.Delete)], EnvironmentPathMode.Append).IsValid(true), "Helper overrides accept stored values only, not delete operations.");
        if (OperatingSystem.IsWindows())
        {
            var privileged = TerminalUserEnvironment.Administrator("C:/test");
            Check(!privileged.ContainsKey("DOTNET_STARTUP_HOOKS") && privileged["PATH"] == Environment.SystemDirectory, "Privileged terminals use the trusted fixed-path baseline.");
        }
        Console.WriteLine("PASS: Workspace terminal latest snapshot, existing-session preservation, closed Helper request shape and administrator isolation.");
        await VerifyLocalWindowsAsync();
    }

    private static async Task VerifyLocalWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        { Console.WriteLine("SKIP: Native ordinary terminal test requires a non-elevated Windows token."); return; }
        var owner = new UserExecutionContext(Guid.NewGuid(), new(HostPlatformKind.Windows, identity.User!.Value, identity.Name,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        using var pty = new LocalUserTerminalPty(new ConPty(), owner)
        { WorkspaceEnvironment = new([new("RELAXKON_WORKSPACE_PROBE", EnvironmentMutationKind.Set, "workspace-child-value")], EnvironmentPathMode.Append) };
        var output = new StringBuilder();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pty.DataReceived += (bytes, count) => { lock (output) { if (output.Length < 8192) output.Append(Encoding.UTF8.GetString(bytes, 0, count)); } };
        pty.ProcessExited += _ => complete.TrySetResult();
        const string privateName = "RELAXKON_SERVER_PRIVATE_PROBE";
        var original = Environment.GetEnvironmentVariable(privateName);
        try
        {
            Environment.SetEnvironmentVariable(privateName, "server-only-value");
            pty.Start(Path.Combine(Environment.SystemDirectory, "cmd.exe"), 80, 24, owner.Identity.HomeDirectory, null,
                ["/d", "/c", "echo %RELAXKON_WORKSPACE_PROBE% & echo %RELAXKON_SERVER_PRIVATE_PROBE%"]);
            await complete.Task.WaitAsync(TimeSpan.FromSeconds(15));
            lock (output)
                Check(output.ToString().Contains("workspace-child-value", StringComparison.Ordinal) && !output.ToString().Contains("server-only-value", StringComparison.Ordinal),
                    "The native child receives Workspace overrides without inheriting Server-only environment.");
            Console.WriteLine("PASS: Native Windows ConPTY child observes Workspace value and excludes Server-only process environment. No host settings changed.");
        }
        finally { Environment.SetEnvironmentVariable(privateName, original); }
    }

    private sealed class CapturingFactory : IPtyFactory
    {
        public int Calls;
        public CapturingPty? Last;
        public IPty Create() { Calls++; return Last = new CapturingPty(); }
    }
    private sealed class CapturingPty : IPty, IWorkspaceTerminalPty
    {
        public TerminalEnvironmentOverrides? WorkspaceEnvironment { get; set; }
        public string DefaultWorkingDirectory => "test-home";
        public bool IsRunning { get; private set; }
        public int ChildPid => 0;
        public event Action<byte[], int>? DataReceived { add { } remove { } }
        public event Action<int>? ProcessExited { add { } remove { } }
        public void Start(string? shell, int columns, int rows, string? workingDirectory, Dictionary<string, string>? environment, IReadOnlyList<string>? arguments)
        { Check(environment is null && workingDirectory == DefaultWorkingDirectory, "Manager never forwards Server process environment or home."); IsRunning = true; }
        public void Write(byte[] data, int offset, int count) { }
        public void Write(string text) { }
        public void Resize(int columns, int rows) { }
        public void Resize(int columns, int rows, int widthPixels, int heightPixels) { }
        public void Stop() => IsRunning = false;
        public void Dispose() => Stop();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
