using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.Server.Tunnels;

public interface ITunnelAudit
{
    Task RecordAsync(string actorUserId, string action, Guid? targetId, string result, string? problemCode, CancellationToken cancellationToken);
    Task<IReadOnlyList<TunnelAuditEntryDto>> ListFrpsAsync(CancellationToken cancellationToken);
}
