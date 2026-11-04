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
public sealed class LocalUserTerminalPty : IPty, IWorkspaceTerminalPty, IDisposable, ITerminalPtyLifetime
{
    private readonly IPty inner;
    private readonly UserExecutionContext context;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _stopGate = new();
    private Task? _stop;
    private bool _started;
    private LinuxTerminalProcessExit? _linuxExit;
    private Exception? _linuxObservationFailure;
    private int _exitSignaled;
    public LocalUserTerminalPty(IPty inner, UserExecutionContext context)
    {
        this.inner = inner; this.context = context;
        inner.ProcessExited += OnInnerExited;
    }
    private void OnInnerExited(int code)
    { _exited.TrySetResult(code); if (Interlocked.Exchange(ref _exitSignaled, 1) == 0) ProcessExited?.Invoke(code); }
    public TerminalEnvironmentOverrides? WorkspaceEnvironment { get; set; }
    public string DefaultWorkingDirectory => context.Identity.HomeDirectory;
    public bool IsRunning => inner.IsRunning;
    public int ChildPid => inner.ChildPid;
    public event Action<byte[], int>? DataReceived { add => inner.DataReceived += value; remove => inner.DataReceived -= value; }
    public event Action<int>? ProcessExited;
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
        _started = true;
        inner.Start(shell, columns, rows, workingDirectory ?? DefaultWorkingDirectory, overrides.Apply(baseline, OperatingSystem.IsWindows()), arguments);
        if (OperatingSystem.IsLinux() && inner is UnixPty)
        {
            try { _linuxExit = LinuxTerminalProcessExit.Capture(inner.ChildPid); }
            catch (Exception error) { _linuxObservationFailure = error; }
        }
        WorkspaceEnvironment = null;
    }
    public void Write(byte[] data, int offset, int count) => inner.Write(data, offset, count);
    public void Write(string text) => inner.Write(text);
    public void Resize(int columns, int rows) => inner.Resize(columns, rows);
    public void Resize(int columns, int rows, int widthPixels, int heightPixels) => inner.Resize(columns, rows, widthPixels, heightPixels);
    public void Stop() { WorkspaceEnvironment = null; inner.Stop(); }
    public void Dispose() { Stop(); (inner as IDisposable)?.Dispose(); }
    public Task StopAsync()
    {
        lock (_stopGate) return _stop ??= Task.Run(async () =>
        {
            Stop();
            if (inner is ITerminalPtyLifetime lifetime) await lifetime.StopAsync();
            else if (_linuxExit is { } observed) { await observed.WaitForExitAsync(); observed.Dispose(); }
            else if (_linuxObservationFailure is not null && !(_exited.Task.IsCompletedSuccessfully && _exited.Task.Result >= 0))
                throw new IOException("terminal.process_exit_unverified", _linuxObservationFailure);
            else if (_started) await _exited.Task;
            (inner as IDisposable)?.Dispose();
            if (_started) OnInnerExited(_exited.Task.IsCompletedSuccessfully ? _exited.Task.Result : -1);
            inner.ProcessExited -= OnInnerExited;
        });
    }
    [DllImport("libc")] private static extern uint geteuid();
}
