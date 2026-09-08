using Microsoft.AspNetCore.SignalR.Client;
using RelaxKonOS.Client.Services.Diagnostics;
using RelaxKonOS.Protocol.Hubs;
using RoyalTerminal.Terminal;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>
/// RoyalTerminal <see cref="ITerminalTransport"/> 的 SignalR 实现：把终端 I/O 桥接到 RelaxKonOS Server 的
/// Terminal Hub。服务端是 PTY 哑中继——本传输只搬运原始字节，VT 渲染由客户端 <c>TerminalControl</c> 完成。
/// </summary>
/// <remarks>
/// <b>断开语义</b>：<see cref="StopAsync"/> 只关闭连接，<b>不</b>调用服务端 <c>Close</c>，故 PTY 存活
/// （用于关窗切换会话 / 桌面关闭 / 网络掉线 —— 再次登录可恢复）。<see cref="KillAsync"/> 才显式终止服务端会话
/// （对应"断开"按钮 / 关闭终端窗口）。
/// </remarks>
public sealed class SignalRTerminalTransport : ITerminalTransport
{
    private readonly SignalRTransportOptions _options;
    private HubConnection? _conn;
    private StartTerminalRequest? _lastRequest;

    public bool IsRunning { get; private set; }

    /// <summary>最近一次 <see cref="StartAsync"/> 附加到的服务端会话 ID（用于列表/切换/恢复）。</summary>
    public string? SessionId { get; private set; }

    // ITerminalTransport events: (buffer, count) 与 exitCode，与服务端 IPty 对齐。
    public event Action<byte[], int>? DataReceived;
    public event Action<int>? ProcessExited;

    public SignalRTerminalTransport(SignalRTransportOptions options) => _options = options;

    public async ValueTask StartAsync(ITerminalTransportOptions options, CancellationToken cancellationToken)
    {
        if (options is not SignalRTransportOptions opts)
            throw new ArgumentException($"Expected {nameof(SignalRTransportOptions)}.", nameof(options));

        _conn = TerminalHubConnection.Build(opts);

        _conn.On<byte[]>(TerminalHubEvents.OnOutput, data => DataReceived?.Invoke(data, data.Length));
        _conn.On<int>(TerminalHubEvents.OnProcessExited, code => ProcessExited?.Invoke(code));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _conn.StartAsync(cancellationToken).ConfigureAwait(false);

        _lastRequest = new StartTerminalRequest(
            opts.Dimensions.Columns, opts.Dimensions.Rows,
            opts.Dimensions.WidthPixels, opts.Dimensions.HeightPixels,
            opts.Shell, opts.WorkingDirectory);

        // Start = attach/create。服务端在返回前会先把缓冲快照经 OnOutput 回放（恢复历史输出）。
            var resp = await _conn.InvokeAsync<AttachTerminalResponse>(
            TerminalHubMethods.Start, _lastRequest, opts.SessionId, cancellationToken)
            .ConfigureAwait(false);
            SessionId = resp?.SessionId;

            IsRunning = true;
            stopwatch.Stop();
            if (opts.Diagnostics is not null)
                TerminalHubConnection.Record(opts.Diagnostics, TerminalHubMethods.Start, stopwatch.Elapsed, NetworkDiagnosticOutcome.Succeeded);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            if (opts.Diagnostics is not null)
                TerminalHubConnection.Record(opts.Diagnostics, TerminalHubMethods.Start, stopwatch.Elapsed,
                    exception is OperationCanceledException ? NetworkDiagnosticOutcome.Cancelled : NetworkDiagnosticOutcome.TransportError,
                    NetworkDiagnosticsService.ErrorKind(exception));
            throw;
        }
    }

    public void SendInput(ReadOnlySpan<byte> utf8)
    {
        var conn = _conn;
        if (conn is null || conn.State != HubConnectionState.Connected) return;
        Fire(conn.InvokeAsync(TerminalHubMethods.Input, utf8.ToArray()));
    }

    public void Resize(TerminalSessionDimensions dimensions)
    {
        var conn = _conn;
        if (conn is null || conn.State != HubConnectionState.Connected) return;
        Fire(conn.InvokeAsync(TerminalHubMethods.Resize,
            dimensions.Columns, dimensions.Rows, dimensions.WidthPixels, dimensions.HeightPixels));
    }

    /// <summary>关闭连接但<b>不</b>终止服务端会话（PTY 存活，供再次登录恢复）。用于关窗 / 切换会话 / 桌面关闭。</summary>
    public async ValueTask StopAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;
        var conn = _conn;
        if (conn is null) return;
        try { await conn.StopAsync().ConfigureAwait(false); } catch { /* best effort */ }
        if (_options.Diagnostics is not null)
            TerminalHubConnection.Record(_options.Diagnostics, "Stop", TimeSpan.Zero, NetworkDiagnosticOutcome.Succeeded);
    }

    /// <summary>显式终止服务端会话（杀 PTY 并从注册表移除）。对应"断开"按钮 / 关闭终端窗口。</summary>
    public async ValueTask KillAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;
        var conn = _conn;
        if (conn is null) return;
        try { await conn.InvokeAsync(TerminalHubMethods.Close).ConfigureAwait(false); } catch { /* best effort */ }
        try { await conn.StopAsync().ConfigureAwait(false); } catch { /* best effort */ }
        if (_options.Diagnostics is not null)
            TerminalHubConnection.Record(_options.Diagnostics, TerminalHubMethods.Close, TimeSpan.Zero, NetworkDiagnosticOutcome.Succeeded);
    }

    /// <summary>用当前活动连接拉取会话列表（需已 <see cref="StartAsync"/>）。</summary>
    public async Task<List<TerminalSessionInfo>> ListSessionsAsync(CancellationToken ct = default)
    {
        var conn = _conn;
        if (conn is null || conn.State != HubConnectionState.Connected)
            return new();
        return await conn.InvokeAsync<List<TerminalSessionInfo>>(
            TerminalHubMethods.ListSessions, ct).ConfigureAwait(false) ?? new();
    }

    public void Dispose()
    {
        IsRunning = false;
        var conn = _conn;
        _conn = null;
        if (conn is not null)
            Fire(conn.DisposeAsync().AsTask());
    }

    /// <summary>观测但不抛出 fire-and-forget 任务的异常（连接断开时 InvokeAsync 可能 fault）。</summary>
    private static void Fire(Task t) =>
        _ = t.ContinueWith(x => { _ = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
}
