using System.Diagnostics;
using System.Text;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.UserExecution;
using RoyalTerminal.Terminal;

namespace RelaxKonOS.Server.Terminal;

/// <summary>Bridges the dedicated Helper-owned user shell PTY to the existing Terminal session.</summary>
public sealed class LinuxUserTerminalPty(UserExecutionContext context, PrivilegedHelperOptions options) : IPty, IDisposable
{
    private Process? _process;
    public bool IsRunning { get; private set; }
    public int ChildPid { get; set; }
    public event Action<byte[], int>? DataReceived;
    public event Action<int>? ProcessExited;
    public void Start(string? shell, int columns, int rows, string? workingDirectory, Dictionary<string, string>? environment, IReadOnlyList<string>? arguments)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(options.HelperPath) || !File.Exists(options.HelperPath)) throw new InvalidOperationException("User terminal Helper is unavailable.");
        var p = new Process { StartInfo = new ProcessStartInfo(options.SudoPath) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        p.StartInfo.ArgumentList.Add("-n"); p.StartInfo.ArgumentList.Add(options.HelperPath); p.StartInfo.ArgumentList.Add("--user-terminal");
        p.Start(); _process = p; IsRunning = true; ChildPid = p.Id;
        var request = new UserExecutionRequest(context.Identity, UserExecutionOperationKind.TerminalStart,
            Path: string.IsNullOrWhiteSpace(workingDirectory) ? context.Identity.HomeDirectory : workingDirectory,
            TerminalShell: shell, OperationId: Guid.NewGuid());
        var line = System.Text.Json.JsonSerializer.Serialize(request, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default) + "\n";
        p.StandardInput.Write(line); p.StandardInput.Flush();
        _ = Task.Run(async () => { var buffer = new byte[65536]; try { while (true) { var read = await p.StandardOutput.BaseStream.ReadAsync(buffer); if (read == 0) break; DataReceived?.Invoke(buffer[..read], read); } } catch { } });
        _ = Task.Run(async () => { await p.WaitForExitAsync(); IsRunning = false; ProcessExited?.Invoke(p.ExitCode); });
    }
    public void Write(byte[] data, int offset, int count) { if (IsRunning && _process is { } p) { p.StandardInput.BaseStream.Write(data, offset, count); p.StandardInput.BaseStream.Flush(); } }
    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text), 0, Encoding.UTF8.GetByteCount(text));
    public void Resize(int columns, int rows) { }
    public void Resize(int columns, int rows, int widthPixels, int heightPixels) { }
    public void Stop() { if (_process is { } p) { try { p.Kill(entireProcessTree: true); } catch { } p.Dispose(); _process = null; } IsRunning = false; }
    public void Dispose() => Stop();
}
