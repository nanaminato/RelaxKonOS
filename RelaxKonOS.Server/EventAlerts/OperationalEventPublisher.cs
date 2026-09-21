using RelaxKonOS.Protocol.EventAlerts;
using RelaxKonOS.Server.Observability;

namespace RelaxKonOS.Server.EventAlerts;

/// <summary>Validates producer input before appending it and updating its alert projection.</summary>
public sealed class OperationalEventPublisher(EventAlertStore store, IObservabilitySanitizer sanitizer)
    : IOperationalEventPublisher
{
    public Task PublishAsync(OperationalEventSignal signal, CancellationToken cancellationToken = default)
    {
        if (!EventAlertCatalog.TryGet(signal.Type, out var definition)
            || string.IsNullOrWhiteSpace(signal.SourceEventKey) || signal.SourceEventKey.Length > 128
            || signal.ResourceId == Guid.Empty || signal.CorrelationId == Guid.Empty || string.IsNullOrWhiteSpace(signal.ProblemCode)
            || signal.ProblemCode.Length > 128)
            throw new ArgumentException("The operational event signal does not match the registered catalog.", nameof(signal));
        if (signal.OperationId == Guid.Empty) throw new ArgumentException("OperationId cannot be empty.", nameof(signal));
        var severity = signal.Severity ?? definition.DefaultSeverity;
        var evidence = string.IsNullOrWhiteSpace(signal.Evidence) ? null : sanitizer.SanitizeSummary(signal.Evidence, store.MaximumEvidenceLength);
        var normalized = new EventAlertStore.AppendRequest(signal.SourceEventKey, definition, signal.ResourceId, signal.CorrelationId,
            signal.ProblemCode, signal.OperationId, severity, evidence, signal.IsRecovery, signal.OccurredAt ?? DateTimeOffset.UtcNow);
        return store.AppendAsync(normalized, cancellationToken);
    }
}
