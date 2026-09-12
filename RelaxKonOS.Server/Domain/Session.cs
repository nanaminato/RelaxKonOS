using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Server.Domain;

/// <summary>服务端 Session 领域模型。表示一次 Device↔Workspace 连接。对应 Authentication.md §12。
/// Session 消失（断开）不等于 Workspace 销毁。生命周期：Created → Active → Disconnected → Expired。</summary>
public sealed class Session
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string AuthenticationMethod { get; set; } = "system";
    public DateTimeOffset AuthenticatedAt { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid DeviceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActiveAt { get; set; }
    public SessionStatus Status { get; set; }

    public SessionDto ToDto() => new(Id, WorkspaceId, DeviceId, CreatedAt, LastActiveAt, Status, UserId, AuthenticationMethod, AuthenticatedAt);
}
