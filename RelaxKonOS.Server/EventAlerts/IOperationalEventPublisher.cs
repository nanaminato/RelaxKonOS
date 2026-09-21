using RelaxKonOS.Protocol.EventAlerts;

namespace RelaxKonOS.Server.EventAlerts;

/// <summary>Safe producer boundary. It accepts a catalog type and controlled identifiers only.</summary>
public interface IOperationalEventPublisher
{
    Task PublishAsync(OperationalEventSignal signal, CancellationToken cancellationToken = default);
}

public sealed record OperationalEventSignal(
    string SourceEventKey,
    string Type,
    Guid ResourceId,
    Guid CorrelationId,
    string ProblemCode,
    Guid? OperationId = null,
    EventAlertSeverity? Severity = null,
    string? Evidence = null,
    bool IsRecovery = false,
    DateTimeOffset? OccurredAt = null);
