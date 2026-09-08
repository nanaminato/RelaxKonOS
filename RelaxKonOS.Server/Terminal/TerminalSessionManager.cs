using System.Collections;
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

    public TerminalSessionManager(
        IPtyFactory ptyFactory,
        IHubContext<TerminalHub, ITerminalHubClient> hub)
    {
        _ptyFactory = ptyFactory;
        _hub = hub;
    }

    /// <summary>
    /// 附加到既有会话（sessionId 命中、归属当前用户、未退出），否则新建。返回会话与是否新建。
    /// </summary>
    public (TerminalSession Session, bool Created) GetOrCreate(
        string userId, string? sessionId, StartTerminalRequest req)
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
        var pty = _ptyFactory.Create();
        var session = new TerminalSession(id, userId, pty, _hub, onExited: Remove);
        _sessions[id] = session;

        var shell = string.IsNullOrWhiteSpace(req.Shell) ? DefaultShell() : req.Shell!;
        var workingDirectory = string.IsNullOrWhiteSpace(req.WorkingDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : req.WorkingDirectory!;

        try
        {
            pty.Start(shell, req.Columns, req.Rows, workingDirectory, BuildEnvironment(), null);
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

    /// <summary>继承宿主进程环境并补一个终端类型，保证 shell 有 PATH 等基础变量。</summary>
    private static Dictionary<string, string> BuildEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry kv in Environment.GetEnvironmentVariables())
            if (kv.Key is string k && kv.Value is string v)
                env[k] = v;
        env["TERM"] = "xterm-256color";
        return env;
    }
}
