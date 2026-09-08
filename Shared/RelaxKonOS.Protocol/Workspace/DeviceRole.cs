namespace RelaxKonOS.Protocol.Workspace;

/// <summary>设备在 Workspace 中的角色。同一 Workspace 同一时刻仅一个 Controller。见 Workspace.md §14-19。</summary>
public enum DeviceRole
{
    Observer,
    Controller
}
