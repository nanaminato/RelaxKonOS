using Microsoft.AspNetCore.SignalR;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Server.Hubs;
using RelaxKonOS.Server.Residency;
using RoyalTerminal.Terminal;

namespace RelaxKonOS.Server.Terminal;

/// <summary>Reconnectable PTY owner; disconnection detaches, never closes the shell.</summary>
public sealed class TerminalSession
{
    internal const int BufferSize = 1024 * 1024, MaximumQueuedBytes = 1024 * 1024;
    internal const int MaximumQueuedItems = 128;
    private readonly object _gate = new();
    private readonly object _ptyGate = new();
    private byte[] _buffer = new byte[BufferSize];
    private int _start, _length, _queuedBytes, _queuedItems;
    private readonly Queue<Output> _output = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _close = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IHubContext<TerminalHub, ITerminalHubClient> _hub;
    private readonly Action<TerminalSession> _onClosed;
    private readonly IModuleWorkLease _lease;
    private readonly Task _writer, _lifetime;
    private string? _connection;
    private Action? _abort;
    private bool _writingComplete;
    private int? _exitCode;
    public string SessionId { get; }
    public string UserId { get; }
    public IPty Pty { get; }
    public bool IsAdministrator { get; init; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public bool HasExited { get; private set; }
    public Task Completion => _done.Task;
    public bool IsAttached { get { lock (_gate) return _connection is not null; } }
    public TerminalSession(string sessionId, string userId, IPty pty, IHubContext<TerminalHub, ITerminalHubClient> hub,
        Action<TerminalSession> onClosed, IModuleWorkLease lease)
    {
        SessionId = sessionId; UserId = userId; Pty = pty; _hub = hub; _onClosed = onClosed; _lease = lease;
        pty.DataReceived += OnData; pty.ProcessExited += OnExited;
        _writer = WriteAsync(); _lifetime = RunAsync();
    }
    internal void FinishStart() => _started.TrySetResult();
    public Task CloseAsync() { _close.TrySetResult(); return Completion; }
    public TerminalSessionInfo ToInfo() => new(SessionId, CreatedAt, HasExited, IsAdministrator);
    internal void ThrowIfClosing()
    {
        _lease.Stopping.ThrowIfCancellationRequested();
        if (_close.Task.IsCompleted) throw new HubException("terminal.session_closing");
    }
    public void Write(byte[] bytes)
    { lock (_ptyGate) { ThrowIfClosing(); Pty.Write(bytes, 0, bytes.Length); } }
    public void Resize(int columns, int rows, int width, int height)
    { lock (_ptyGate) { ThrowIfClosing(); Pty.Resize(columns, rows, width, height); } }
    public async ValueTask AttachAsync(string connection, Action abort)
    {
        Output? replay = null;
        lock (_gate)
        {
            ThrowIfClosing();
            if (_length > MaximumQueuedBytes - _queuedBytes || _queuedItems >= MaximumQueuedItems) throw new HubException("terminal.slow_consumer");
            _connection = connection; _abort = abort;
            if (_length > 0) replay = Enqueue(connection, abort, Snapshot(), null);
        }
        if (replay is not null) await replay.Done.Task;
    }
    public void Detach(string connection)
    { lock (_gate) if (_connection == connection) { _connection = null; _abort = null; } }
    private void OnData(byte[] buffer, int count)
    {
        if (count <= 0 || count > buffer.Length) return;
        Action? abort = null;
        lock (_gate)
        {
            if (_writingComplete) return;
            Append(buffer, count);
            if (_connection is not { } connection) return;
            if (count > MaximumQueuedBytes - _queuedBytes || _queuedItems >= MaximumQueuedItems) { abort = _abort; _connection = null; _abort = null; }
            else Enqueue(connection, _abort!, buffer.AsSpan(0, count).ToArray(), null);
        }
        TryAbort(abort);
    }
    private void OnExited(int code)
    { lock (_gate) { HasExited = true; _exitCode = code; } _close.TrySetResult(); }
    private Output Enqueue(string connection, Action abort, byte[]? data, int? code)
    {
        var item = new Output(connection, abort, data, code); _output.Enqueue(item);
        _queuedBytes += data?.Length ?? 0; _queuedItems++; _signal.Release(); return item;
    }
    private async Task WriteAsync()
    {
        while (true)
        {
            await _signal.WaitAsync(); Output? item;
            lock (_gate) { if (!_output.TryDequeue(out item)) { if (_writingComplete) return; else continue; } }
            Exception? failure = null;
            try
            {
                if (item.Data is { } bytes) await _hub.Clients.Client(item.Connection).OnOutput(bytes);
                else await _hub.Clients.Client(item.Connection).OnProcessExited(item.Code!.Value);
            }
            catch (Exception error)
            { Detach(item.Connection); TryAbort(item.Abort); failure = error; }
            finally { lock (_gate) { _queuedBytes -= item.Data?.Length ?? 0; _queuedItems--; } }
            if (failure is null) item.Done.TrySetResult();
            else { item.Done.TrySetException(failure); _ = item.Done.Task.Exception; }
        }
    }
    private async Task RunAsync()
    {
        try
        {
            try { await _close.Task.WaitAsync(_lease.Stopping); }
            catch (OperationCanceledException) when (_lease.Stopping.IsCancellationRequested) { _close.TrySetResult(); }
            await _started.Task;
            if (Pty is not ITerminalPtyLifetime lifetime) throw new InvalidOperationException("terminal.cleanup_unverified");
            Task cleanup; lock (_ptyGate) cleanup = lifetime.StopAsync();
            await cleanup;
            Pty.DataReceived -= OnData; Pty.ProcessExited -= OnExited;
            lock (_gate)
            {
                if (_connection is { } connection && _exitCode is { } code) Enqueue(connection, _abort!, null, code);
                _writingComplete = true; _connection = null; _abort = null; _signal.Release();
            }
            await _writer;
            lock (_gate) { _buffer = []; _length = _start = 0; }
            _onClosed(this); _lease.Dispose(); _done.TrySetResult();
        }
        catch (Exception error)
        {
            try { _lease.RequireRecovery(); }
            catch (Exception recovery) { error = new AggregateException(error, recovery); }
            _done.TrySetException(error); _ = _done.Task.Exception;
        }
    }
    private static void TryAbort(Action? abort) { try { abort?.Invoke(); } catch { } }
    private void Append(byte[] data, int count)
    {
        if (count >= BufferSize) { Array.Copy(data, count - BufferSize, _buffer, 0, BufferSize); _start = 0; _length = BufferSize; return; }
        if (count > BufferSize - _length) { var remove = count - (BufferSize - _length); _start = (_start + remove) % BufferSize; _length -= remove; }
        var write = (_start + _length) % BufferSize; var first = Math.Min(BufferSize - write, count);
        Array.Copy(data, 0, _buffer, write, first); if (count > first) Array.Copy(data, first, _buffer, 0, count - first); _length += count;
    }
    private byte[] Snapshot()
    {
        var data = new byte[_length]; var first = Math.Min(BufferSize - _start, _length);
        Array.Copy(_buffer, _start, data, 0, first); if (first < _length) Array.Copy(_buffer, 0, data, first, _length - first); return data;
    }
    private sealed record Output(string Connection, Action Abort, byte[]? Data, int? Code)
    { internal TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }
}
