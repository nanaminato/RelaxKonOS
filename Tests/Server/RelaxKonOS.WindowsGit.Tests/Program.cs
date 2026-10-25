using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.UserExecution;

if (!OperatingSystem.IsWindows()) { Console.WriteLine("Windows Git tests require Windows."); return; }
if (args is [var serverMode, var gitExecutable, var repositoryRoot, var portText, var reportPath]
    && serverMode is "--http-git-server" or "--https-git-server")
{
    await HttpGitAcceptanceServer.RunAsync(gitExecutable, repositoryRoot, int.Parse(portText), reportPath, serverMode == "--https-git-server");
    return;
}
if (args is ["--worker", var mode, var workerPidPath])
{
    if (mode == "flood")
    {
        Console.Write(new string('x', 64 * 1024));
        Console.Out.Flush();
    }
    else if (mode == "tree")
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in WorkerArguments("--worker", "sleep", workerPidPath)) start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        await File.WriteAllTextAsync(workerPidPath, child.Id.ToString());
    }
    await Task.Delay(TimeSpan.FromSeconds(30));
    return;
}
var count = 0;
void Check(bool success, string message)
{
    if (!success) throw new Exception(message);
    Console.WriteLine($"PASS WINDOWS GIT {++count}: {message}");
}

// Use the Windows argument parser to independently check quoting of empty, Unicode, embedded
// quote, trailing slash and shell metacharacter arguments. No shell is involved in the launcher.
var values = new[] { "git.exe", "", "测试 & user", "a\\\"b", "C:\\with space\\", "&|<>^%!()'", "plain\\" };
var command = string.Join(' ', values.Select(WindowsUserGit.QuoteArgument));
var argv = Native.CommandLineToArgvW(command, out var argc);
if (argv == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
try
{
    Check(argc == values.Length && values.Select((value, index) => value == Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size))).All(equal => equal),
        "Windows parses every argument exactly, including empty values and trailing backslashes");
}
finally { Native.LocalFree(argv); }

using var current = WindowsIdentity.GetCurrent();
var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var identity = new UserExecutionIdentity(HostPlatformKind.Windows, current.User!.Value, current.Name, home);

// Verify production Execute dispatch and S4U when explicitly run as LocalSystem with a target
// account. Default runs exercise the same native launcher with a restricted current-user token.
if (args.Length == 4 && args[0] == "--s4u")
{
    identity = new UserExecutionIdentity(HostPlatformKind.Windows, args[1], args[2], args[3]);
    var response = await WindowsUserExecutionExecutor.ExecuteAsync(new(identity, UserExecutionOperationKind.GitExecute,
        Path: identity.HomeDirectory, GitArguments: ["--version"], OperationId: Guid.NewGuid()), default);
    Check(response.Success, $"LocalSystem/S4U dispatch succeeds: {response.ProblemCode}");
    var result = JsonSerializer.Deserialize<WindowsUserGit.Result>(Convert.FromBase64String(response.OutputBase64!), RelaxKonOSJsonOptions.Default)!;
    Check(result.Success && result.Output.StartsWith("git version"), "S4U Git returns a real version result");
    Console.WriteLine($"Windows Git S4U checks passed: {count}.");
    return;
}

var root = Path.Combine(home, "AppData", "Local", "Temp", $"relaxkonos-windows-git-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
var environment = new ProcessStartInfo().Environment.ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);
var git = WindowsUserGit.ResolveGit(environment);

if (!Native.CreateRestrictedToken(current.AccessToken, 1, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out var restricted))
    throw new Win32Exception(Marshal.GetLastWin32Error());
using (restricted)
using (var primary = WindowsUserProcessToken.Create(restricted, identity.StableIdentity))
{
    WindowsUserGit.Result Run(params string[] arguments) => WindowsUserGit.Run(primary, git, arguments, root, environment, default);
    Check(Run("--version").Success, "Native CreateProcessAsUser Git launcher succeeds");
    Check(Run("init", "-b", "main").Success, "Native Git initializes a repository");
    Check(Run("config", "user.name", "Windows Helper 测试").Success && Run("config", "user.email", "test@example.invalid").Success, "Test repository has an isolated commit identity");
    Check(Run("config", "credential.helper", "").Success, "Test repository disables credential helpers without touching a real credential store");
    await File.WriteAllTextAsync(Path.Combine(root, "文件 & name.txt"), "contents\n");
    Check(Run("add", "--", "文件 & name.txt").Success && Run("commit", "-m", "commit \"quoted\" & 中文").Success,
        "Native launcher preserves Unicode paths and quoted commit messages");
    Check(Run("log", "-1", "--format=%s").Output.TrimEnd() == "commit \"quoted\" & 中文", "Git stored the exact commit message");
    Check(!Run("rev-parse", "--verify", "missing-ref").Success, "Nonzero exit and stderr are returned");

    {
        Check(Run("config", "credential.helper", "!echo username=wrong; echo password=wrong").Success,
            "Fixture supplies a conflicting host credential helper");
        // An HTTP 401 endpoint exercises Git + askpass inside the actual native launcher,
        // without adding credential plumbing or arbitrary stdin to the wire contract.
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var authenticated = false;
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, leaveOpen: true);
                    var expected = "Authorization: Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("测试 & user:token &|<>^%!\"'()"));
                    while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 } line)
                        if (line.Equals(expected, StringComparison.OrdinalIgnoreCase)) authenticated = true;
                    var reply = "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"test\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(reply), stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = WindowsUserGit.Execute(restricted, new(identity, UserExecutionOperationKind.GitExecute,
                Path: root, GitArguments: ["fetch", $"http://127.0.0.1:{port}/repo?a&b"],
                GitCredentials: new("测试 & user", "token &|<>^%!\"'()"), OperationId: Guid.NewGuid()), home, deadline.Token);
            Check(!result.Success && authenticated, "Native Git sends exact UTF-8 askpass credentials to a local HTTP challenge");
        }
        finally { stop.Cancel(); listener.Stop(); await server; }
    }

    // Child process identity must be the supplied token, never the service/process identity.
    var who = WindowsUserGit.Run(primary, Path.Combine(Environment.SystemDirectory, "whoami.exe"), ["/user", "/fo", "csv", "/nh"], root, environment, default);
    Check(who.Success && who.Output.Contains(identity.StableIdentity), "Child process has the supplied user's SID");

    var allowed = WindowsUserGit.Execute(restricted, new(identity, UserExecutionOperationKind.GitExecute,
        Path: root, GitArguments: ["status", "--porcelain"], OperationId: Guid.NewGuid()), home, default);
    Check(allowed.Success && allowed.Output.Length == 0, "Closed Git operation runs through user environment and impersonated setup");
    try
    {
        WindowsUserGit.Execute(restricted, new(identity, UserExecutionOperationKind.GitExecute,
            Path: root, GitArguments: ["clone", "--upload-pack=evil", "url", "path"], OperationId: Guid.NewGuid()), home, default);
        throw new Exception("Dangerous Git operation was accepted.");
    }
    catch (ArgumentException) { Check(true, "Closed runner rejects dangerous transport arguments before creating a child"); }

    // A real descendant keeps stdout inherited. Cancelling must close the entire job, otherwise
    // this call would hang while draining the child's inherited pipe for thirty seconds.
    var pidPath = Path.Combine(root, "descendant.pid");
    using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
    {
        var ready = Task.Run(async () =>
        {
            while (!File.Exists(pidPath)) await Task.Delay(20, cancel.Token);
            cancel.Cancel();
        });
        var watch = Stopwatch.StartNew();
        try
        {
            WindowsUserGit.Run(primary, Environment.ProcessPath!, WorkerArguments("--worker", "tree", pidPath), root, environment, cancel.Token);
            throw new Exception("Cancellation was ignored.");
        }
        catch (OperationCanceledException) { Check(watch.Elapsed < TimeSpan.FromSeconds(5), "Cancellation promptly terminates the job and drains both pipes"); }
        await ready;
        var childId = int.Parse(await File.ReadAllTextAsync(pidPath));
        try
        {
            using var child = Process.GetProcessById(childId);
            Check(child.WaitForExit(3000), "Cancellation terminates descendants too");
        }
        catch (ArgumentException) { Check(true, "Cancellation terminates descendants too"); }
    }
    try
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        WindowsUserGit.Run(primary, Environment.ProcessPath!, WorkerArguments("--worker", "flood", "unused"), root, environment, deadline.Token, maximumOutputBytes: 1024);
        throw new Exception("Output overflow was accepted.");
    }
    catch (WindowsUserGit.OutputTooLargeException) { Check(true, "Output overflow kills the child without unbounded buffering"); }
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        try
        {
            WindowsUserGit.Run(primary, "does-not-exist.exe", [], root, environment, cancelled.Token);
            throw new Exception("Pre-cancelled launch was accepted.");
        }
        catch (OperationCanceledException) { Check(true, "Pre-cancelled operation does not start a process"); }
    }
}
Console.WriteLine($"Windows Git checks passed: {count}. Temporary repository: {root}");

static string[] WorkerArguments(params string[] arguments) => Path.GetFileNameWithoutExtension(Environment.ProcessPath!)
    .Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? [typeof(Native).Assembly.Location, .. arguments] : arguments;

static class Native
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateRestrictedToken(SafeAccessTokenHandle existing, uint flags, uint disabledCount, IntPtr disabled,
        uint deletedCount, IntPtr deleted, uint restrictedCount, IntPtr restricted, out SafeAccessTokenHandle token);
}

