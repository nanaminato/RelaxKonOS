namespace RelaxKonOS.Protocol.Hubs;

/// <summary>Workspace Hub 的 client→server invoke 方法名常量。Client 端 HubConnection.InvokeAsync 用。</summary>
public static class WorkspaceHubMethods
{
    /// <summary>加入 Workspace Group，返回当前快照与角色。</summary>
    public const string JoinWorkspace = nameof(JoinWorkspace);

    /// <summary>离开 Workspace Group。</summary>
    public const string LeaveWorkspace = nameof(LeaveWorkspace);

    /// <summary>广播桌面状态增量（仅 Controller 可调）。</summary>
    public const string SendDesktopStateChange = nameof(SendDesktopStateChange);

    /// <summary>Observer 请求控制权。</summary>
    public const string RequestControl = nameof(RequestControl);

    /// <summary>Controller 主动释放控制权。</summary>
    public const string ReleaseControl = nameof(ReleaseControl);

    /// <summary>心跳：续租 + 刷新 Session.LastActiveAt。</summary>
    public const string Heartbeat = nameof(Heartbeat);
}
