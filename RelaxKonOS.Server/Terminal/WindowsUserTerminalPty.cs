using System.IO.Pipes;
using System.Text;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.UserExecution;
using RoyalTerminal.Terminal;

namespace RelaxKonOS.Server.Terminal;

/// <summary>Persistent connection to a Helper-owned ConPTY running as the authenticated OS user.</summary>
public sealed class WindowsUserTerminalPty(UserExecutionContext context,
    WindowsNamedPipeUserExecutionTransport transport) : IPty, IWorkspaceTerminalPty, IDisposable
{
    public RelaxKonOS.Protocol.Settings.TerminalEnvironmentOverrides? WorkspaceEnvironment { get; set; }
    private readonly object _writeLock = new();
    private NamedPipeClientStream? _pipe;
    private int _exitSignaled;
    public string DefaultWorkingDirectory => context.Identity.HomeDirectory;
    public bool IsAdministrator { get; set; }
    public bool IsRunning { get; private set; }
    public int ChildPid { get; set; }
    public event Action<byte[], int>? DataReceived;
    public event Action<int>? ProcessExited;

    public void Start(string? shell, int columns, int rows, string? workingDirectory,
        Dictionary<string, string>? environment, IReadOnlyList<string>? arguments)
    {
        UserTerminalStreamProtocol.ValidateDimensions(columns, rows, 0, 0);
        if (environment is not null) throw new InvalidOperationException("terminal.environment_must_be_resolved");
        var operationId = Guid.NewGuid();
        var request = new UserExecutionRequest(context.Identity, UserExecutionOperationKind.TerminalStart,
            TerminalAdministrator: IsAdministrator,
            TerminalEnvironment: IsAdministrator ? null : WorkspaceEnvironment,
            Path: string.IsNullOrWhiteSpace(workingDirectory) ? DefaultWorkingDirectory : workingDirectory,
            TerminalShell: shell, TerminalColumns: columns, TerminalRows: rows,
            TerminalWidthPixels: 0, TerminalHeightPixels: 0, OperationId: operationId,
            Correlation: CorrelationContext.Create(operationId, "user.execution"));
        var pipe = transport.OpenTerminalAsync(request).GetAwaiter().GetResult();
        WorkspaceEnvironment = null;
        _pipe = pipe;
        IsRunning = true;
        _ = Task.Run(async () =>
        {
            var buffer = new byte[65536];
            try
            {
                while (true)
                {
                    var read = await pipe.ReadAsync(buffer).ConfigureAwait(false);
                    if (read == 0) break;
                    DataReceived?.Invoke(buffer[..read], read);
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            finally
            {
                IsRunning = false;
                pipe.Dispose();
                if (Interlocked.Exchange(ref _exitSignaled, 1) == 0) ProcessExited?.Invoke(-1);
            }
        });
    }

    public void Write(byte[] data, int offset, int count)
    {
        lock (_writeLock)
        {
            if (!IsRunning || _pipe is not { } pipe) return;
            while (count > 0)
            {
                var length = Math.Min(count, UserExecutionProtocol.MaximumTerminalInputBytes);
                UserTerminalStreamProtocol.WriteInput(pipe, data.AsSpan(offset, length));
                offset += length;
                count -= length;
            }
        }
    }
    public void Write(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Write(bytes, 0, bytes.Length);
    }
    public void Resize(int columns, int rows) => Resize(columns, rows, 0, 0);
    public void Resize(int columns, int rows, int widthPixels, int heightPixels)
    {
        lock (_writeLock)
            if (IsRunning && _pipe is { } pipe)
                UserTerminalStreamProtocol.WriteResize(pipe, columns, rows, widthPixels, heightPixels);
    }
    public void Stop()
    {
        WorkspaceEnvironment = null;
        IsRunning = false;
        Interlocked.Exchange(ref _pipe, null)?.Dispose();
    }
    public void Dispose() => Stop();
}
