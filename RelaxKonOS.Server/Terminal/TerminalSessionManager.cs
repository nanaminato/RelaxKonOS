using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Protocol.Hubs;
using RoyalTerminal.Terminal;
using RelaxKonOS.Server.Hubs;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Terminal;

/// <summary>
/// 持久终端会话注册表（Singleton）。按 <see cref="TerminalSession.SessionId"/> 索引、按 UserId 归属。
/// PTY 由本管理器持有，独立于 Hub 连接生命周期：连接断开不会移除会话，只有显式 <see cref="RemoveAsync"/>
/// （客户端 Close / 关闭终端窗口）或子进程退出才清理。
/// </summary>
public sealed class TerminalSessionManager : IHostedService, IDisposable
{
    private readonly SemaphoreSlim _creation = new(1, 1);
    private volatile bool _stopping;
    private readonly IResidentModuleWorkLeaseSource _leases;
    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new();
    private readonly IPtyFactory _ptyFactory;
    private readonly IHubContext<TerminalHub, ITerminalHubClient> _hub;
    private readonly Settings.WorkspaceEnvironmentService _environment;
    private readonly IServiceScopeFactory _scopeFactory;

    public TerminalSessionManager(
        IPtyFactory ptyFactory,
        IHubContext<TerminalHub, ITerminalHubClient> hub,
        Settings.WorkspaceEnvironmentService environment, IServiceScopeFactory scopeFactory, IResidentModuleWorkLeaseSource leases)
    {
        _ptyFactory = ptyFactory;
        _hub = hub;
        _environment = environment;
        _scopeFactory = scopeFactory;
        _leases = leases;
    }

    /// <summary>
    /// 附加到既有会话（sessionId 命中、归属当前用户、未退出），否则新建。返回会话与是否新建。
    /// </summary>
    public async Task<(TerminalSession Session, bool Created)> GetOrCreateAsync(
        string userId, string? sessionId, StartTerminalRequest req, bool administrator = false, CancellationToken ct = default)
    {
        await _creation.WaitAsync(ct);
        try
        {
        if (_stopping) throw new InvalidOperationException("terminal.host_stopping");
        // 1) 尝试附加既有会话
        if (!string.IsNullOrWhiteSpace(sessionId)
            && _sessions.TryGetValue(sessionId, out var existing)
            && existing.UserId == userId
            && !existing.HasExited)
        {
            existing.ThrowIfClosing();
            return (existing, false);
        }

        // 2) 新建：spawn PTY
        var id = Guid.NewGuid().ToString("N");
        var lease = await _leases.AcquireAsync(ServerModules.TerminalId, ct);
        IPty pty;
        try { ct.ThrowIfCancellationRequested(); lease.Stopping.ThrowIfCancellationRequested();
            if (_stopping) throw new InvalidOperationException("terminal.host_stopping");
            pty = administrator && _ptyFactory is PlatformPtyFactory platform ? platform.CreateAdministrator() : _ptyFactory.Create(); }
        catch { lease.Dispose(); throw; }
        if (administrator)
        {
            if (pty is not WindowsUserTerminalPty administratorPty)
            {
                (pty as IDisposable)?.Dispose();
                lease.Dispose();
                throw new PlatformNotSupportedException("Administrator terminals require Windows System Mode with the Helper backend.");
            }
            administratorPty.IsAdministrator = true;
        }
        var session = new TerminalSession(id, userId, pty, _hub, onClosed: Remove, lease) { IsAdministrator = administrator };
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
                if (!Guid.TryParse(userId, out var owner))
                    throw new InvalidOperationException("terminal.workspace_not_found");
                using var scope = _scopeFactory.CreateScope();
                var workspaces = scope.ServiceProvider.GetRequiredService<Storage.IWorkspaceRepository>();
                if (workspaces.FindByUserId(owner) is not { } workspace || workspace.UserId != owner)
                    throw new InvalidOperationException("terminal.workspace_not_found");
                var snapshot = _environment.Read(workspace);
                if (pty is not IWorkspaceTerminalPty configurable) throw new InvalidOperationException("terminal.environment_backend_unavailable");
                configurable.WorkspaceEnvironment = new(snapshot.Variables.Select(value => new RelaxKonOS.Protocol.Settings.EnvironmentMutation(value.Name,
                    RelaxKonOS.Protocol.Settings.EnvironmentMutationKind.Set, value.RawValue, value.ValueKind)).ToArray(), snapshot.PathMode);
            }
            ct.ThrowIfCancellationRequested(); lease.Stopping.ThrowIfCancellationRequested();
            if (_stopping) throw new InvalidOperationException("terminal.host_stopping");
            pty.Start(shell, req.Columns, req.Rows, workingDirectory, null, null);
            ct.ThrowIfCancellationRequested(); lease.Stopping.ThrowIfCancellationRequested();
        }
        catch
        {
            // 启动失败：回滚，不留半启动会话
            session.FinishStart();
            await session.CloseAsync();
            throw;
        }

        session.FinishStart();
        return (session, true);
        }
        finally { _creation.Release(); }
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
    public Task RemoveAsync(string sessionId)
    {
        return _sessions.TryGetValue(sessionId, out var session) ? session.CloseAsync() : Task.CompletedTask;
    }

    /// <summary>ProcessExited 回调路径：从字典移除（PTY 已自行退出，无需再 Kill）。</summary>
    private void Remove(TerminalSession session) => _sessions.TryRemove(session.SessionId, out _);

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken ct)
    {
        _stopping = true;
        await _creation.WaitAsync(ct);
        Task[] close;
        try { close = _sessions.Values.Select(session => session.CloseAsync()).ToArray(); }
        finally { _creation.Release(); }
        await Task.WhenAll(close).WaitAsync(ct);
    }
    public void Dispose()
    {
        _stopping = true;
        foreach (var session in _sessions.Values) _ = session.CloseAsync();
    }

    private static string DefaultShell() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "powershell" : "bash";

}
