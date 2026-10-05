using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.SignalR;
using RelaxKonOS.Protocol.Hubs;
using RoyalTerminal.Terminal;
using RelaxKonOS.Server.Hubs;

namespace RelaxKonOS.Server.Terminal;

/// <summary>
/// 持久终端会话注册表（Singleton）。按 <see cref="TerminalSession.SessionId"/> 索引、按 UserId 归属。
/// PTY 由本管理器持有，独立于 Hub 连接生命周期：连接断开不会移除会话，只有显式 <see cref="Remove"/>
/// （客户端 Close / 关闭终端窗口）或子进程退出才清理。
/// </summary>
public sealed class TerminalSessionManager
{
    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new();
    private readonly IPtyFactory _ptyFactory;
    private readonly IHubContext<TerminalHub, ITerminalHubClient> _hub;
    private readonly Settings.WorkspaceEnvironmentService _environment;
    private readonly Storage.IWorkspaceRepository _workspaces;

    public TerminalSessionManager(
        IPtyFactory ptyFactory,
        IHubContext<TerminalHub, ITerminalHubClient> hub,
        Settings.WorkspaceEnvironmentService environment, Storage.IWorkspaceRepository workspaces)
    {
        _ptyFactory = ptyFactory;
        _hub = hub;
        _environment = environment; _workspaces = workspaces;
    }

    /// <summary>
    /// 附加到既有会话（sessionId 命中、归属当前用户、未退出），否则新建。返回会话与是否新建。
    /// </summary>
    public (TerminalSession Session, bool Created) GetOrCreate(
        string userId, string? sessionId, StartTerminalRequest req, bool administrator = false)
    {
        // 1) 尝试附加既有会话
        if (!string.IsNullOrWhiteSpace(sessionId)
            && _sessions.TryGetValue(sessionId, out var existing)
            && existing.UserId == userId
            && !existing.HasExited)
        {
            return (existing, false);
        }

        // 2) 新建：spawn PTY
        var id = Guid.NewGuid().ToString("N");
        var pty = administrator && _ptyFactory is PlatformPtyFactory platform ? platform.CreateAdministrator() : _ptyFactory.Create();
        if (administrator)
        {
            if (pty is not WindowsUserTerminalPty administratorPty)
            {
                (pty as IDisposable)?.Dispose();
                throw new PlatformNotSupportedException("Administrator terminals require Windows System Mode with the Helper backend.");
            }
            administratorPty.IsAdministrator = true;
        }
        var session = new TerminalSession(id, userId, pty, _hub, onExited: Remove) { IsAdministrator = administrator };
        _sessions[id] = session;

        var shell = string.IsNullOrWhiteSpace(req.Shell) ? DefaultShell() : req.Shell!;
        // A System Mode Linux PTY belongs to the authenticated effective OS user, not the Server
        // service account. Keep an explicit Explorer directory intact, but otherwise start in the
        // effective user's home rather than inheriting the Server process's current environment.
        var workingDirectory = string.IsNullOrWhiteSpace(req.WorkingDirectory)
            ? pty is IWorkspaceTerminalPty userPty
                ? userPty.DefaultWorkingDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : req.WorkingDirectory!;

        try
        {
            if (!administrator)
            {
                if (!Guid.TryParse(userId, out var owner) || _workspaces.FindByUserId(owner) is not { } workspace || workspace.UserId != owner)
                    throw new InvalidOperationException("terminal.workspace_not_found");
                var snapshot = _environment.Read(workspace);
                if (pty is not IWorkspaceTerminalPty configurable) throw new InvalidOperationException("terminal.environment_backend_unavailable");
                configurable.WorkspaceEnvironment = new(snapshot.Variables.Select(value => new RelaxKonOS.Protocol.Settings.EnvironmentMutation(value.Name,
                    RelaxKonOS.Protocol.Settings.EnvironmentMutationKind.Set, value.RawValue, value.ValueKind)).ToArray(), snapshot.PathMode);
            }
            pty.Start(shell, req.Columns, req.Rows, workingDirectory, null, null);
        }
        catch
        {
            // 启动失败：回滚，不留半启动会话
            _sessions.TryRemove(id, out _);
            session.Kill();
            throw;
        }

        return (session, true);
    }

    public bool TryGet(string sessionId, out TerminalSession? session) =>
        _sessions.TryGetValue(sessionId, out session);

    public List<TerminalSessionInfo> ListForUser(string userId) =>
        _sessions.Values
            .Where(s => s.UserId == userId)
            .OrderBy(s => s.CreatedAt)
            .Select(s => s.ToInfo())
            .ToList();

    /// <summary>手动终止并移除会话（杀 PTY）。</summary>
    public void Remove(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
            session.Kill();
    }

    /// <summary>ProcessExited 回调路径：从字典移除（PTY 已自行退出，无需再 Kill）。</summary>
    private void Remove(TerminalSession session) => _sessions.TryRemove(session.SessionId, out _);

    private static string DefaultShell() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "powershell" : "bash";

}
