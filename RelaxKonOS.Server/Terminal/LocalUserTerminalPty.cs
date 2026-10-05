using System.Runtime.InteropServices;
using System.Security.Principal;
using RelaxKonOS.Protocol.Settings;
using RelaxKonOS.Server.UserExecution;
using RoyalTerminal.Terminal;

namespace RelaxKonOS.Server.Terminal;

public interface IWorkspaceTerminalPty
{
    TerminalEnvironmentOverrides? WorkspaceEnvironment { get; set; }
    string DefaultWorkingDirectory { get; }
}

/// <summary>Local identity backend, with a freshly constructed target-user environment at each spawn.</summary>
public sealed class LocalUserTerminalPty(IPty inner, UserExecutionContext context) : IPty, IWorkspaceTerminalPty, IDisposable
{
    public TerminalEnvironmentOverrides? WorkspaceEnvironment { get; set; }
    public string DefaultWorkingDirectory => context.Identity.HomeDirectory;
    public bool IsRunning => inner.IsRunning;
    public int ChildPid => inner.ChildPid;
    public event Action<byte[], int>? DataReceived { add => inner.DataReceived += value; remove => inner.DataReceived -= value; }
    public event Action<int>? ProcessExited { add => inner.ProcessExited += value; remove => inner.ProcessExited -= value; }
    public void Start(string? shell, int columns, int rows, string? workingDirectory, Dictionary<string, string>? environment, IReadOnlyList<string>? arguments)
    {
        if (environment is not null) throw new InvalidOperationException("terminal.environment_must_be_resolved");
        Dictionary<string, string> baseline;
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new InvalidOperationException("terminal.nonprivileged_identity_required");
            if (identity.User?.Value != context.Identity.StableIdentity) throw new InvalidOperationException("terminal.identity_mismatch");
            baseline = TerminalUserEnvironment.Windows(identity.AccessToken, context.Identity.HomeDirectory);
            if (string.IsNullOrWhiteSpace(shell) || shell == "powershell") shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        }
        else if (OperatingSystem.IsLinux())
        {
            if (geteuid() == 0) throw new InvalidOperationException("terminal.nonprivileged_identity_required");
            if (geteuid().ToString(System.Globalization.CultureInfo.InvariantCulture) != context.Identity.StableIdentity) throw new InvalidOperationException("terminal.identity_mismatch");
            if (string.IsNullOrWhiteSpace(shell) || shell == "bash") shell = "/bin/bash";
            baseline = TerminalUserEnvironment.Linux(context.Identity.HomeDirectory, context.Identity.CanonicalAccount, shell!);
        }
        else throw new PlatformNotSupportedException();
        var overrides = WorkspaceEnvironment ?? new([], EnvironmentPathMode.Append);
        inner.Start(shell, columns, rows, workingDirectory ?? DefaultWorkingDirectory, overrides.Apply(baseline, OperatingSystem.IsWindows()), arguments);
        WorkspaceEnvironment = null;
    }
    public void Write(byte[] data, int offset, int count) => inner.Write(data, offset, count);
    public void Write(string text) => inner.Write(text);
    public void Resize(int columns, int rows) => inner.Resize(columns, rows);
    public void Resize(int columns, int rows, int widthPixels, int heightPixels) => inner.Resize(columns, rows, widthPixels, heightPixels);
    public void Stop() { WorkspaceEnvironment = null; inner.Stop(); }
    public void Dispose() { Stop(); (inner as IDisposable)?.Dispose(); }
    [DllImport("libc")] private static extern uint geteuid();
}
