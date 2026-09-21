using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.EventAlerts;

/// <summary>Cursor-paged response. A null cursor means the end of the immutable ordering.</summary>
public sealed record EventAlertPageDto<T>(
    [property: JsonPropertyName("items")] IReadOnlyList<T> Items,
    [property: JsonPropertyName("nextCursor")] string? NextCursor);

/// <summary>Only enumerated target data crosses the wire; clients build and validate local URIs.</summary>
public sealed record RemediationTargetDto(
    [property: JsonPropertyName("kind")] RemediationTargetKind Kind,
    [property: JsonPropertyName("resourceId")] Guid? ResourceId = null,
    [property: JsonPropertyName("operationId")] Guid? OperationId = null);

public sealed record OperationalEventDto(
    [property: JsonPropertyName("eventId")] Guid EventId,
    [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("severity")] EventAlertSeverity Severity,
    [property: JsonPropertyName("source")] OperationalEventSource Source,
    [property: JsonPropertyName("outcome")] OperationalEventOutcome Outcome,
    [property: JsonPropertyName("problemCode")] string ProblemCode,
    [property: JsonPropertyName("correlationId")] Guid CorrelationId,
    [property: JsonPropertyName("operationId")] Guid? OperationId,
    [property: JsonPropertyName("resourceType")] string ResourceType,
    [property: JsonPropertyName("resourceReference")] string ResourceReference,
    [property: JsonPropertyName("evidence")] string? Evidence,
    [property: JsonPropertyName("remediationTarget")] RemediationTargetDto RemediationTarget);

public sealed record OperationalAlertDto(
    [property: JsonPropertyName("alertId")] Guid AlertId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("severity")] EventAlertSeverity Severity,
    [property: JsonPropertyName("status")] OperationalAlertStatus Status,
    [property: JsonPropertyName("firstOccurredAt")] DateTimeOffset FirstOccurredAt,
    [property: JsonPropertyName("lastOccurredAt")] DateTimeOffset LastOccurredAt,
    [property: JsonPropertyName("occurrenceCount")] int OccurrenceCount,
    [property: JsonPropertyName("lastEventId")] Guid LastEventId,
    [property: JsonPropertyName("problemCode")] string ProblemCode,
    [property: JsonPropertyName("acknowledgedAt")] DateTimeOffset? AcknowledgedAt,
    [property: JsonPropertyName("acknowledgedByReference")] string? AcknowledgedByReference,
    [property: JsonPropertyName("resolutionReason")] string? ResolutionReason,
    [property: JsonPropertyName("remediationTarget")] RemediationTargetDto RemediationTarget);

public sealed record OperationalAlertDetailDto(
    [property: JsonPropertyName("alert")] OperationalAlertDto Alert,
    [property: JsonPropertyName("events")] IReadOnlyList<OperationalEventDto> Events,
    [property: JsonPropertyName("actions")] IReadOnlyList<AlertActionDto> Actions);

public sealed record AlertActionDto(
    [property: JsonPropertyName("actionId")] Guid ActionId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("actorReference")] string? ActorReference,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

public sealed record EventAlertSummaryDto(
    [property: JsonPropertyName("openCount")] int OpenCount,
    [property: JsonPropertyName("acknowledgedCount")] int AcknowledgedCount,
    [property: JsonPropertyName("unacknowledgedCriticalCount")] int UnacknowledgedCriticalCount,
    [property: JsonPropertyName("highestUnacknowledgedSeverity")] EventAlertSeverity? HighestUnacknowledgedSeverity,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset? UpdatedAt);

public sealed record AcknowledgeAlertRequest([property: JsonPropertyName("note")] string? Note);
public sealed record ResolveAlertRequest([property: JsonPropertyName("reason")] string Reason);
public sealed record SuppressAlertRequest(
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

/// <summary>Durable hub payload; callers must re-read the authoritative REST state.</summary>
public sealed record AlertChangedDto(
    [property: JsonPropertyName("alertId")] Guid AlertId,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("status")] OperationalAlertStatus Status,
    [property: JsonPropertyName("severity")] EventAlertSeverity Severity,
    [property: JsonPropertyName("occurrenceCount")] int OccurrenceCount);
