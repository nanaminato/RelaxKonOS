using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Server.Terminal;
using RelaxKonOS.Server.UserExecution;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Hubs;

/// <summary>
/// Remote Terminal Hub —— 持久会话哑中继。PTY 由 <see cref="TerminalSessionManager"/> 持有，与 Hub 连接解耦：
/// <list type="bullet">
/// <item><see cref="Start"/>：附加到既有会话（先回放缓冲快照）或新建 PTY 会话，返回会话 ID 与是否新建。</item>
/// <item><see cref="Input"/>/<see cref="Resize"/>：转发到当前附加会话的 PTY。</item>
/// <item><see cref="CloseSession"/>：手动终止——按会话 ID 杀掉并移除（会话必须属于当前用户）。</item>
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

    private readonly IHostAccountPrivilegeService _privileges;
    public TerminalHub(TerminalSessionManager manager, IHostAccountPrivilegeService privileges)
    { _manager = manager; _privileges = privileges; }

    public Task<AttachTerminalResponse> StartAdministrator(StartTerminalRequest req, string? sessionId)
    {
        RequireAdministrator();
        return StartCore(req, sessionId, true);
    }

    /// <summary>附加到远端 PTY 会话。方法名 <c>Start</c> 与 <see cref="TerminalHubMethods.Start"/> 对齐。</summary>
    /// <remarks>
    /// <paramref name="sessionId"/> 刻意<b>不</b>写 C# 默认值：SignalR 按参数个数匹配方法，不会应用默认值，
    /// 客户端少传一个参数会整次调用失败（客户端只会看到笼统的 invoke 错误）。没有默认值才能让"必须传两个参数"
    /// 在签名上直接可见。附带既有会话用 <see cref="AttachExisting"/>，语义相同但找不到时会失败而不是新建。
    /// </remarks>
    public async Task<AttachTerminalResponse> Start(StartTerminalRequest req, string? sessionId)
        => await StartCore(req, sessionId, false);

    private async Task<AttachTerminalResponse> StartCore(StartTerminalRequest req, string? sessionId, bool administrator)
    {
        var userId = Context.UserIdentifier
            ?? throw new HubException("未认证的连接：缺少用户标识。");

        TerminalSession session;
        bool created;
        if (sessionId is not null && _manager.TryGet(sessionId, out var existing)
            && existing?.UserId == userId)
        {
            if (existing.IsAdministrator) RequireAdministrator();
            if (existing.IsAdministrator != administrator)
                throw new HubException("terminal.session_mode_mismatch");
        }
        try { (session, created) = _manager.GetOrCreate(userId, sessionId, req, administrator); }
        catch (UserExecutionException exception)
        {
            throw new HubException($"terminal.start_failed: {exception.ProblemCode}: {exception.Message}");
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException
            or InvalidOperationException or System.ComponentModel.Win32Exception or IOException
            or OperationCanceledException)
        {
            // Report the operational reason without enabling SignalR detailed errors globally.
            throw new HubException(exception is OperationCanceledException
                ? "terminal.start_failed: Windows user terminal Helper timed out."
                : $"terminal.start_failed: {exception.Message}");
        }

        // 附加当前连接并回放缓冲快照（恢复历史输出）。失败则不留下半附加状态。
        GetCurrentSession()?.Detach(Context.ConnectionId);
        await session.AttachAsync(Context.ConnectionId).ConfigureAwait(false);
        Context.Items[SidKey] = session.SessionId;

        return new AttachTerminalResponse(session.SessionId, created);
    }

    public async Task<AttachTerminalResponse> AttachExisting(string sessionId)
    {
        var userId = Context.UserIdentifier ?? throw new HubException("未认证的连接：缺少用户标识。");
        if (!_manager.TryGet(sessionId, out var session) || session is null ||
            session.UserId != userId || session.HasExited)
            throw new HubException("terminal.session_not_found");
        if (session.IsAdministrator) RequireAdministrator();
        GetCurrentSession()?.Detach(Context.ConnectionId);
        await session.AttachAsync(Context.ConnectionId).ConfigureAwait(false);
        Context.Items[SidKey] = session.SessionId;
        return new AttachTerminalResponse(session.SessionId, false);
    }

    /// <summary>向当前会话的 PTY 写入用户输入字节。</summary>
    public Task Input(byte[] data)
    {
        if (GetCurrentSession()?.IsAdministrator == true) RequireAdministrator();
        if (GetCurrentSession() is { } session && data.Length > 0)
            session.Pty.Write(data, 0, data.Length);
        return Task.CompletedTask;
    }

    /// <summary>调整当前会话 PTY 的尺寸。</summary>
    public Task Resize(int columns, int rows, int widthPixels, int heightPixels)
    {
        if (GetCurrentSession()?.IsAdministrator == true) RequireAdministrator();
        if (GetCurrentSession() is { } session)
            session.Pty.Resize(columns, rows, widthPixels, heightPixels);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 终止指定会话：杀 PTY 并从注册表移除。对应客户端的「关闭会话」——一个会话一个按钮，
    /// 不要求在关闭前先附加到它上面（否则关掉一个后台会话就得先切过去、丢失当前视图）。
    /// </summary>
    public Task CloseSession(string sessionId)
    {
        var userId = Context.UserIdentifier;
        if (userId is null || string.IsNullOrWhiteSpace(sessionId))
            return Task.CompletedTask;

        // 会话 ID 来自客户端，所以归属必须在这里核对：只按 ID 删除会让任何已认证用户靠猜 ID 杀掉别人的 PTY。
        if (!_manager.TryGet(sessionId, out var session) || session is null || session.UserId != userId)
            throw new HubException("terminal.session_not_found");

        if (Context.Items.TryGetValue(SidKey, out var sid) && sid is string current && current == sessionId)
            Context.Items.Remove(SidKey);

        _manager.Remove(sessionId);
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

    private void RequireAdministrator()
    {
        if (!OperatingSystem.IsWindows() || Context.User is null
            || _privileges.Classify(Context.User) != HostAccountPrivilege.HostAdministrator)
            throw new HubException("terminal.administrator_required: Sign in with the Windows administrator account and its system password.");
    }
}
