namespace RelaxKonOS.Protocol.Workspace;

/// <summary>Workspace 生命周期状态。见 Workspace.md §8（Created → Running → Idle → Sleeping → ...）。</summary>
public enum WorkspaceState
{
    Created,
    Running,
    Idle,
    Sleeping
}
