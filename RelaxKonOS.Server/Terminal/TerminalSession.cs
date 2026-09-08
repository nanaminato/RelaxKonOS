using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using RelaxKonOS.Protocol.Hubs;
using RoyalTerminal.Terminal;
using RelaxKonOS.Server.Hubs;

namespace RelaxKonOS.Server.Terminal;

/// <summary>
/// 一个持久终端会话：拥有一个 <see cref="IPty"/> 与 1MB 环形输出缓冲。PTY 生命周期与 Hub 连接解耦——
/// 连接断开（<see cref="Detach"/>) 只清空当前附加连接，PTY 继续运行、缓冲继续累积；只有 <see cref="Kill"/>
/// （客户端显式断开 / 关闭终端窗口）或子进程退出才终止。再次 <see cref="Attach"/> 时先把缓冲快照回放给客户端，
/// 实现"再次登录看到原桌面"。
/// </summary>
/// <remarks>
/// <see cref="IPty.DataReceived"/> 在 ConPTY 读线程触发，此时 Hub 实例早已释放，不能再用 this.Context；
/// 故会话持有 <see cref="IHubContext{THub,TClient}"/>，按 <see cref="CurrentConnectionId"/> 主动推送。
/// </remarks>
public sealed class TerminalSession
{
    private const int BufferSize = 1024 * 1024; // 1MB，与参考项目一致

    private readonly IHubContext<TerminalHub, ITerminalHubClient> _hub;
    private readonly Action<TerminalSession> _onExited;
    private readonly object _bufferLock = new();
    private readonly byte[] _outputBuffer = new byte[BufferSize];
    private int _bufferStart;
    private int _bufferLength;

    private readonly object _connLock = new();
    private string? _currentConnectionId;

    public string SessionId { get; }
    public string UserId { get; }
    public IPty Pty { get; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public bool HasExited { get; private set; }

    public TerminalSession(
        string sessionId,
        string userId,
        IPty pty,
        IHubContext<TerminalHub, ITerminalHubClient> hub,
        Action<TerminalSession> onExited)
    {
        SessionId = sessionId;
        UserId = userId;
        Pty = pty;
        _hub = hub;
        _onExited = onExited;

        // PTY 输出：始终入缓冲（哪怕无人连接，shell 也不阻塞），并在有附加连接时转发。
        pty.DataReceived += OnPtyDataReceived;
        pty.ProcessExited += OnPtyProcessExited;
    }

    public bool IsAttached
    {
        get { lock (_connLock) return _currentConnectionId is not null; }
    }

    /// <summary>附加一条 Hub 连接：记录 connectionId，并先把缓冲快照回放（恢复历史输出）。</summary>
    public async ValueTask AttachAsync(string connectionId)
    {
        string? prev;
        lock (_connLock)
        {
            prev = _currentConnectionId;
            _currentConnectionId = connectionId;
        }
        // 若此前已有别的连接附加（异常路径），先静默忽略；正常流程不会出现。
        _ = prev;

        var snapshot = GetBufferSnapshot();
        if (snapshot.Length > 0)
        {
            try { await _hub.Clients.Client(connectionId).OnOutput(snapshot).ConfigureAwait(false); }
            catch { /* client may have already gone */ }
        }
    }

    /// <summary>连接断开：仅当 connectionId 匹配当前附加连接时清空，<b>不</b>停止 PTY。</summary>
    public void Detach(string connectionId)
    {
        lock (_connLock)
        {
            if (_currentConnectionId == connectionId)
                _currentConnectionId = null;
        }
    }

    /// <summary>手动终止：停止并释放 PTY。由客户端 Close 调用或服务端清理触发。</summary>
    public void Kill()
    {
        lock (_connLock)
            _currentConnectionId = null;
        try { Pty.Stop(); } catch { /* best effort */ }
        try { (Pty as IDisposable)?.Dispose(); } catch { /* best effort */ }
    }

    public TerminalSessionInfo ToInfo() => new(SessionId, CreatedAt, HasExited);

    private void OnPtyDataReceived(byte[] buffer, int count)
    {
        if (count <= 0) return;
        var data = count == buffer.Length ? buffer : buffer[..count];

        // 1) 始终入环形缓冲（恢复用）
        AppendToBuffer(data, 0, count);

        // 2) 有附加连接则转发原始字节（VT 渲染在客户端）
        string? connId;
        lock (_connLock) connId = _currentConnectionId;
        if (connId is null) return;
        // data 可能就是 buffer 本身（Count==Length），转发副本避免被后续覆写；count<Length 时 data 已是新切片。
        var forward = count == buffer.Length ? data.ToArray() : data;
        try { _ = _hub.Clients.Client(connId).OnOutput(forward); }
        catch { /* client gone; ignore */ }
    }

    private void OnPtyProcessExited(int exitCode)
    {
        HasExited = true;
        string? connId;
        lock (_connLock) connId = _currentConnectionId;
        if (connId is not null)
        {
            try { _ = _hub.Clients.Client(connId).OnProcessExited(exitCode); }
            catch { /* ignore */ }
        }
        _onExited(this);
    }

    private void AppendToBuffer(byte[] data, int offset, int count)
    {
        lock (_bufferLock)
        {
            if (count >= BufferSize)
            {
                Array.Copy(data, offset + count - BufferSize, _outputBuffer, 0, BufferSize);
                _bufferStart = 0;
                _bufferLength = BufferSize;
                return;
            }

            var freeSpace = BufferSize - _bufferLength;
            if (count > freeSpace)
            {
                var removeCount = count - freeSpace;
                _bufferStart = (_bufferStart + removeCount) % BufferSize;
                _bufferLength -= removeCount;
            }

            var writePos = (_bufferStart + _bufferLength) % BufferSize;
            var firstPart = Math.Min(BufferSize - writePos, count);
            Array.Copy(data, offset, _outputBuffer, writePos, firstPart);
            if (count > firstPart)
                Array.Copy(data, offset + firstPart, _outputBuffer, 0, count - firstPart);
            _bufferLength += count;
        }
    }

    private byte[] GetBufferSnapshot()
    {
        lock (_bufferLock)
        {
            var snapshot = new byte[_bufferLength];
            if (_bufferLength == 0) return snapshot;

            if (_bufferStart + _bufferLength <= BufferSize)
            {
                Array.Copy(_outputBuffer, _bufferStart, snapshot, 0, _bufferLength);
            }
            else
            {
                var firstPart = BufferSize - _bufferStart;
                Array.Copy(_outputBuffer, _bufferStart, snapshot, 0, firstPart);
                Array.Copy(_outputBuffer, 0, snapshot, firstPart, _bufferLength - firstPart);
            }
            return snapshot;
        }
    }
}
