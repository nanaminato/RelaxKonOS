namespace RelaxKonOS.Protocol.Hubs;

/// <summary>Workspace Hub 的 server→client 事件名常量。对应 IWorkspaceHubClient 接口方法名。Client 端 HubConnection.On&lt;T&gt; 用。</summary>
public static class WorkspaceHubEvents
{
    /// <summary>桌面状态变更。</summary>
    public const string OnDesktopStateChanged = nameof(IWorkspaceHubClient.OnDesktopStateChanged);

    /// <summary>控制权变更。</summary>
    public const string OnControllerChanged = nameof(IWorkspaceHubClient.OnControllerChanged);

    /// <summary>设备上线。</summary>
    public const string OnDeviceConnected = nameof(IWorkspaceHubClient.OnDeviceConnected);

    /// <summary>设备下线。</summary>
    public const string OnDeviceDisconnected = nameof(IWorkspaceHubClient.OnDeviceDisconnected);

    /// <summary>Session 状态变更。</summary>
    public const string OnSessionUpdated = nameof(IWorkspaceHubClient.OnSessionUpdated);

    /// <summary>Workspace 状态变更。</summary>
    public const string OnWorkspaceStateChanged = nameof(IWorkspaceHubClient.OnWorkspaceStateChanged);
}
