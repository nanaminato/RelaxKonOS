using System.Diagnostics;
using System.Text;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.UserExecution;
using RoyalTerminal.Terminal;

namespace RelaxKonOS.Server.Terminal;

/// <summary>Bridges the dedicated Helper-owned user shell PTY to the existing Terminal session.</summary>
public sealed class LinuxUserTerminalPty(UserExecutionContext context, PrivilegedHelperOptions options) : IPty, IDisposable
{
    private readonly object _inputLock = new();
    private Process? _process;
    private int _exitSignaled;
    public bool IsRunning { get; private set; }
    public int ChildPid { get; set; }
    public event Action<byte[], int>? DataReceived;
    public event Action<int>? ProcessExited;
    public void Start(string? shell, int columns, int rows, string? workingDirectory, Dictionary<string, string>? environment, IReadOnlyList<string>? arguments)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(options.HelperPath) || !File.Exists(options.HelperPath)) throw new InvalidOperationException("User terminal Helper is unavailable.");
        UserTerminalStreamProtocol.ValidateDimensions(columns, rows, 0, 0);
        var p = new Process { StartInfo = new ProcessStartInfo(options.SudoPath) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        p.StartInfo.ArgumentList.Add("-n"); p.StartInfo.ArgumentList.Add(options.HelperPath); p.StartInfo.ArgumentList.Add("--user-terminal");
        TrustedProcessEnvironment.Apply(p.StartInfo);
        p.Start(); _process = p; IsRunning = true; ChildPid = p.Id;
        var request = new UserExecutionRequest(context.Identity, UserExecutionOperationKind.TerminalStart,
            Path: string.IsNullOrWhiteSpace(workingDirectory) ? context.Identity.HomeDirectory : workingDirectory,
            TerminalShell: shell, TerminalColumns: columns, TerminalRows: rows,
            TerminalWidthPixels: 0, TerminalHeightPixels: 0, OperationId: Guid.NewGuid());
        var line = System.Text.Json.JsonSerializer.Serialize(request, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default) + "\n";
        p.StandardInput.Write(line); p.StandardInput.Flush();
        _ = Task.Run(async () => { var buffer = new byte[65536]; try { while (true) { var read = await p.StandardOutput.BaseStream.ReadAsync(buffer); if (read == 0) break; DataReceived?.Invoke(buffer[..read], read); } } catch { } });
        _ = Task.Run(async () => { try { await p.StandardError.ReadToEndAsync(); } catch { } });
        _ = Task.Run(async () =>
        {
            var exitCode = -1;
            try { await p.WaitForExitAsync(); exitCode = p.ExitCode; } catch { }
            finally
            {
                IsRunning = false;
                if (Interlocked.Exchange(ref _exitSignaled, 1) == 0) ProcessExited?.Invoke(exitCode);
                p.Dispose();
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
}
