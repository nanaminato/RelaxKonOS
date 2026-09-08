namespace RelaxKonOS.Server.Domain;

/// <summary>Sanitized audit trail for high-risk tunnel operations. It intentionally has no detail/payload column.</summary>
public sealed class TunnelAuditEntry
{
    public Guid Id { get; set; }
    public string ActorUserId { get; set; } = "";
    public string Action { get; set; } = "";
    public Guid? TargetId { get; set; }
    public string Result { get; set; } = "";
    public string? ProblemCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
