namespace RelaxKonOS.Protocol.Workspace;

/// <summary>Session 生命周期状态。见 Security.md §16（Created → Active → Disconnected → Expired）。</summary>
public enum SessionStatus
{
    Created,
    Active,
    Disconnected,
    Expired
}
