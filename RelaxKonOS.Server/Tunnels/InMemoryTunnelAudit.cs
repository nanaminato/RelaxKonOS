using System.Collections.Concurrent;
using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.Server.Tunnels;

/// <summary>Development-only audit fallback for the in-memory storage provider.</summary>
public sealed class InMemoryTunnelAudit : ITunnelAudit
{
    private readonly ConcurrentQueue<(DateTimeOffset Timestamp, string Action, string Result, string ProblemCode)> _entries = new();
    public Task RecordAsync(string actorUserId, string action, Guid? targetId, string result, string? problemCode, CancellationToken cancellationToken)
    {
        _entries.Enqueue((DateTimeOffset.UtcNow, action, result, problemCode ?? ""));
        while (_entries.Count > 200) _entries.TryDequeue(out _);
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<TunnelAuditEntryDto>> ListFrpsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TunnelAuditEntryDto>>(_entries.Where(x => x.Action.StartsWith("frps.")).OrderByDescending(x => x.Timestamp).Select(x => new TunnelAuditEntryDto(x.Timestamp, x.Action, x.Result, x.ProblemCode)).ToArray());
}
