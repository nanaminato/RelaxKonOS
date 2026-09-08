using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Server.Domain;

/// <summary>服务端 Workspace 领域模型。One User One Persistent Workspace。对应 Authentication.md §11。
/// Controller* 字段实现 Active Controller + Grace Period 模型（见 Workspace.md §14-19）。</summary>
public sealed class Workspace
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public WorkspaceState State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? ControllerDeviceId { get; set; }
    public DateTimeOffset? ControllerGrantedAt { get; set; }
    public DateTimeOffset? ControllerLeaseExpiresAt { get; set; }

    public WorkspaceDto ToDto() => new(Id, UserId, Name, State, CreatedAt, ToControllerLease());

    private ControllerLeaseInfo? ToControllerLease()
    {
        if (ControllerDeviceId is not { } did
            || ControllerGrantedAt is not { } granted
            || ControllerLeaseExpiresAt is not { } exp)
            return null;
        return new ControllerLeaseInfo(did, granted, exp);
    }
}
