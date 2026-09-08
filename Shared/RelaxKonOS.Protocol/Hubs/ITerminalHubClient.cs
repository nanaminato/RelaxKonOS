namespace RelaxKonOS.Protocol.Hubs;

/// <summary>
/// Terminal Hub 的 server→client 接口。Server 端用 <c>Hub&lt;ITerminalHubClient&gt;</c> 获得编译期校验；
/// Client 端用 <c>HubConnection.On&lt;T&gt;</c> 注册回调，事件名见 <see cref="TerminalHubEvents"/>。
/// </summary>
/// <remarks>
/// 服务端是 PTY 哑中继：只回传 PTY 原始输出字节与退出码；VT 解析（标题/响铃/光标）全部在客户端完成，
/// 故本契约不含 TitleChanged 等语义事件。
/// </remarks>
public interface ITerminalHubClient
{
    /// <summary>PTY 原始输出字节流（server→client）。</summary>
    Task OnOutput(byte[] data);

    /// <summary>PTY 子进程退出（exitCode）。</summary>
    Task OnProcessExited(int exitCode);
}
