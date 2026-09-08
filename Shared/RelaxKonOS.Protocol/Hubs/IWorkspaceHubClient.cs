using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Protocol.Hubs;

/// <summary>
/// Workspace Hub 的 server→client 接口。Server 端用 <c>Hub&lt;IWorkspaceHubClient&gt;</c> 获得编译期校验；
/// Client 端用 <c>HubConnection.On&lt;T&gt;</c> 注册回调，事件名见 <see cref="WorkspaceHubEvents"/>。
/// </summary>
public interface IWorkspaceHubClient
{
    /// <summary>桌面状态变更广播（Controller 发起 patch，广播给同 Workspace 其他设备）。</summary>
    Task OnDesktopStateChanged(DesktopStatePatch patch);

    /// <summary>控制权变更通知。</summary>
    Task OnControllerChanged(ControllerChangedEventArgs e);

    /// <summary>设备上线通知。</summary>
    Task OnDeviceConnected(DevicePresenceEventArgs e);

    /// <summary>设备下线通知。</summary>
    Task OnDeviceDisconnected(DevicePresenceEventArgs e);

    /// <summary>Session 状态变更通知（Active→Disconnected 等）。</summary>
    Task OnSessionUpdated(SessionDto session);

    /// <summary>Workspace 整体状态变更通知（Running→Idle→Sleeping）。</summary>
    Task OnWorkspaceStateChanged(WorkspaceState state);
}
