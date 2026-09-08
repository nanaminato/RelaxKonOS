using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Transport.Pty;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>
/// 自包含的终端传输工厂：对 <see cref="SignalRTransportOptions"/> 返回 SignalR 传输（远端 PTY），
/// 其余选项（如 <c>PtyTransportOptions</c> 本地回退）委托内部 <c>CompositeTerminalTransportFactory</c>。
/// 持有最近创建的 SignalR 传输引用，供窗口关闭/切换会话时
/// <see cref="StopActiveAsync"/>（仅关连接，PTY 存活）或"断开"按钮
/// <see cref="KillActiveAsync"/>（杀 PTY）调用。
/// </summary>
/// <remarks>
/// 本工厂在 <c>TerminalControl</c> 构造时通过 9 参数构造函数注入（<c>TerminalTransportFactory</c> 属性只读），
/// 故无需在运行时替换控件的传输工厂。
/// </remarks>
public sealed class SignalRTransportFactory : ITerminalTransportFactory
{
    private readonly ITerminalTransportFactory _inner;
    private SignalRTerminalTransport? _current;

    public SignalRTransportFactory()
    {
        // Local PTY 回退：仅需 PtyTerminalTransportProvider（平台 ConPTY/forkpty 由传递依赖提供）。
        _inner = new CompositeTerminalTransportFactory(
            new ITerminalTransportProvider[] { new PtyTerminalTransportProvider() });
    }

    public ITerminalTransport Create(ITerminalTransportOptions options)
    {
        if (options is SignalRTransportOptions s)
        {
            _current = new SignalRTerminalTransport(s);
            return _current;
        }
        return _inner.Create(options);
    }

    /// <summary>最近创建的 SignalR 传输附加到的服务端会话 ID（供 VM 跟踪/切换/清理）。无活动传输时为 null。</summary>
    public string? CurrentSessionId => _current?.SessionId;

    /// <summary>停止并释放最近创建的 SignalR 传输（仅关闭连接，<b>不</b>终止服务端 PTY —— 用于关窗/切换会话/桌面关闭）。</summary>
    public async ValueTask StopActiveAsync()
    {
        var t = _current;
        _current = null;
        if (t is null) return;
        try { await t.StopAsync(); } catch { /* best effort */ }
        t.Dispose();
    }

    /// <summary>显式终止服务端会话并释放传输（杀 PTY —— 用于"断开"按钮 / 关闭终端窗口）。</summary>
    public async ValueTask KillActiveAsync()
    {
        var t = _current;
        _current = null;
        if (t is null) return;
        try { await t.KillAsync(); } catch { /* best effort */ }
        t.Dispose();
    }
}
