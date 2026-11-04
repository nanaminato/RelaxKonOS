using System.Diagnostics;
using System.Text;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.UserExecution;
using RoyalTerminal.Terminal;

namespace RelaxKonOS.Server.Terminal;

/// <summary>Bridges the dedicated Helper-owned user shell PTY to the existing Terminal session.</summary>
public sealed class LinuxUserTerminalPty(UserExecutionContext context, PrivilegedHelperOptions options) : IPty, IWorkspaceTerminalPty, IDisposable, ITerminalPtyLifetime
{
    public RelaxKonOS.Protocol.Settings.TerminalEnvironmentOverrides? WorkspaceEnvironment { get; set; }
    private readonly object _inputLock = new();
    private Process? _process;
    private Process? _ownedProcess;
    private int _exitSignaled;
    private Task _reader = Task.CompletedTask, _errors = Task.CompletedTask, _exit = Task.CompletedTask;
    private readonly object _stopGate = new();
    private Task? _stop;
    private bool _terminalExitConfirmed;
    private int _terminalExitCode = -1;
    /// <summary>Default directory for a terminal whose authenticated identity owns this PTY.</summary>
    public string DefaultWorkingDirectory => context.Identity.HomeDirectory;
    public bool IsRunning { get; private set; }
    public int ChildPid { get; set; }
    public event Action<byte[], int>? DataReceived;
    public event Action<int>? ProcessExited;
    public void Start(string? shell, int columns, int rows, string? workingDirectory, Dictionary<string, string>? environment, IReadOnlyList<string>? arguments)
    {
        if (environment is not null) throw new InvalidOperationException("terminal.environment_must_be_resolved");
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(options.HelperPath) || !File.Exists(options.HelperPath)) throw new InvalidOperationException("User terminal Helper is unavailable.");
        UserTerminalStreamProtocol.ValidateDimensions(columns, rows, 0, 0);
        var p = new Process { StartInfo = new ProcessStartInfo(options.SudoPath) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        p.StartInfo.ArgumentList.Add("-n"); p.StartInfo.ArgumentList.Add(options.HelperPath); p.StartInfo.ArgumentList.Add("--user-terminal");
        TrustedProcessEnvironment.Apply(p.StartInfo);
        p.Start(); _process = p; _ownedProcess = p; IsRunning = true; ChildPid = p.Id;
        var operationId = Guid.NewGuid();
        var request = new UserExecutionRequest(context.Identity, UserExecutionOperationKind.TerminalStart,
            TerminalEnvironment: WorkspaceEnvironment,
            Path: string.IsNullOrWhiteSpace(workingDirectory) ? context.Identity.HomeDirectory : workingDirectory,
            TerminalShell: shell, TerminalColumns: columns, TerminalRows: rows,
            TerminalWidthPixels: 0, TerminalHeightPixels: 0, OperationId: operationId,
            Correlation: CorrelationContext.Create(operationId, "user.execution"));
        var line = System.Text.Json.JsonSerializer.Serialize(request, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default) + "\n";
        p.StandardInput.Write(line); p.StandardInput.Flush();
        WorkspaceEnvironment = null;
        _reader = Task.Run(async () =>
        {
            _terminalExitCode = await TerminalOutputStream.ReadAsync(p.StandardOutput.BaseStream, (bytes, count) => DataReceived?.Invoke(bytes, count));
            _terminalExitConfirmed = true;
        });
        _errors = Task.Run(async () => { await p.StandardError.BaseStream.CopyToAsync(Stream.Null); });
        _exit = Task.Run(async () =>
        {
            var exitCode = -1;
            try
            {
                await p.WaitForExitAsync();
                await Task.WhenAll(_reader, _errors);
                if (!_terminalExitConfirmed) throw new IOException("terminal.exit_confirmation_missing");
                exitCode = _terminalExitCode;
            }
            finally
            {
                IsRunning = false;
                if (Interlocked.Exchange(ref _exitSignaled, 1) == 0) ProcessExited?.Invoke(exitCode);
            }
        });
    }
    public void Write(byte[] data, int offset, int count)
    {
        if (!IsRunning || _process is not { } p || count <= 0) return;
        lock (_inputLock)
        {
            try
            {
                while (count > 0)
                {
                    var length = Math.Min(count, UserExecutionProtocol.MaximumTerminalInputBytes);
                    UserTerminalStreamProtocol.WriteInput(p.StandardInput.BaseStream, data.AsSpan(offset, length));
                    offset += length;
                    count -= length;
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }
    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text), 0, Encoding.UTF8.GetByteCount(text));
    public void Resize(int columns, int rows) => Resize(columns, rows, 0, 0);
    public void Resize(int columns, int rows, int widthPixels, int heightPixels)
    {
        if (!IsRunning || _process is not { } p) return;
        lock (_inputLock)
        {
            try { UserTerminalStreamProtocol.WriteResize(p.StandardInput.BaseStream, columns, rows, widthPixels, heightPixels); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }
    public void Stop()
    {
        WorkspaceEnvironment = null;
        if (_process is { } p)
        {
            lock (_inputLock)
            {
                try { UserTerminalStreamProtocol.WriteClose(p.StandardInput.BaseStream); } catch { }
            }
            try { p.Kill(entireProcessTree: true); } catch { }
            _process = null;
        }
        IsRunning = false;
    }
    public void Dispose() => Stop();
    public Task StopAsync()
    {
        lock (_stopGate) return _stop ??= Task.Run(async () =>
        {
            var process = _ownedProcess;
            // Cooperative close lets the Helper reap its PTY before the Helper process exits.
            if (process is not null && !process.HasExited)
                lock (_inputLock)
                {
                    UserTerminalStreamProtocol.WriteClose(process.StandardInput.BaseStream);
                    process.StandardInput.Close();
                }
            // Startup can fail before the normal exit-reader tasks have been published.
            if (process is not null) await process.WaitForExitAsync();
            await Task.WhenAll(_reader, _errors, _exit);
            process?.Dispose(); _process = null; _ownedProcess = null; IsRunning = false;
        });
    }
}
