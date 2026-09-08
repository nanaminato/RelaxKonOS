using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Server.Terminal;

namespace RelaxKonOS.Server.Hubs;

/// <summary>
/// Remote Terminal Hub —— 持久会话哑中继。PTY 由 <see cref="TerminalSessionManager"/> 持有，与 Hub 连接解耦：
/// <list type="bullet">
/// <item><see cref="Start"/>：附加到既有会话（先回放缓冲快照）或新建 PTY 会话，返回会话 ID 与是否新建。</item>
/// <item><see cref="Input"/>/<see cref="Resize"/>：转发到当前附加会话的 PTY。</item>
/// <item><see cref="Close"/>：手动终止——杀掉当前会话并从注册表移除（对应客户端"断开"按钮 / 关闭终端窗口）。</item>
/// <item><see cref="ListSessions"/>：返回当前用户的全部终端会话摘要（多实例）。</item>
/// <item><see cref="OnDisconnectedAsync"/>：仅 detach 当前连接，<b>不</b>终止 PTY（网络掉线 / 桌面关闭 / 进程退出 → 保活，供再次登录恢复）。</item>
/// </list>
/// VT 解析（标题/响铃/光标/颜色）全部在客户端完成；服务端只搬运原始字节。
/// </summary>
[Authorize]
public sealed class TerminalHub : Hub<ITerminalHubClient>
{
    private const string SidKey = "sid";

    private readonly TerminalSessionManager _manager;

    public TerminalHub(TerminalSessionManager manager) => _manager = manager;

    /// <summary>附加到远端 PTY 会话。方法名 <c>Start</c> 与 <see cref="TerminalHubMethods.Start"/> 对齐。</summary>
    public async Task<AttachTerminalResponse> Start(StartTerminalRequest req, string? sessionId = null)
    {
        var userId = Context.UserIdentifier
            ?? throw new HubException("未认证的连接：缺少用户标识。");

        var (session, created) = _manager.GetOrCreate(userId, sessionId, req);

        // 附加当前连接并回放缓冲快照（恢复历史输出）。失败则不留下半附加状态。
        await session.AttachAsync(Context.ConnectionId).ConfigureAwait(false);
        Context.Items[SidKey] = session.SessionId;

        return new AttachTerminalResponse(session.SessionId, created);
    }

    /// <summary>向当前会话的 PTY 写入用户输入字节。</summary>
    public Task Input(byte[] data)
    {
        if (GetCurrentSession() is { } session && data.Length > 0)
            session.Pty.Write(data, 0, data.Length);
        return Task.CompletedTask;
    }

    /// <summary>调整当前会话 PTY 的尺寸。</summary>
    public Task Resize(int columns, int rows, int widthPixels, int heightPixels)
    {
        if (GetCurrentSession() is { } session)
            session.Pty.Resize(columns, rows, widthPixels, heightPixels);
        return Task.CompletedTask;
    }

    /// <summary>手动终止当前会话：杀 PTY 并从注册表移除。对应客户端"断开"按钮 / 关闭终端窗口。</summary>
    public Task Close()
    {
        if (Context.Items.TryGetValue(SidKey, out var sid) && sid is string id)
        {
            Context.Items.Remove(SidKey);
            _manager.Remove(id);
        }
        return Task.CompletedTask;
    }

    /// <summary>拉取当前用户的全部终端会话摘要。</summary>
    public Task<List<TerminalSessionInfo>> ListSessions()
    {
        var userId = Context.UserIdentifier;
        var list = userId is null
            ? new List<TerminalSessionInfo>()
            : _manager.ListForUser(userId);
        return Task.FromResult(list);
    }

    /// <summary>连接断开：仅 detach，保留 PTY 供再次登录恢复。</summary>
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (GetCurrentSession() is { } session)
            session.Detach(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private TerminalSession? GetCurrentSession()
    {
        if (Context.Items.TryGetValue(SidKey, out var sid) && sid is string id
            && _manager.TryGet(id, out var s))
            return s;
        return null;
    }
}
