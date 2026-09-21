using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Observability;

public enum ObservabilityOutcome { Started, Succeeded, Denied, Failed, Cancelled, Recovered }
public enum ObservabilitySeverity { Trace, Debug, Information, Warning, Error, Critical }

/// <summary>Sanitized, append-only security-audit payload.</summary>
public sealed record SecurityAuditEvent(
    [property: JsonPropertyName("eventId")] int EventId,
    [property: JsonPropertyName("eventName")] string EventName,
    [property: JsonPropertyName("outcome")] ObservabilityOutcome Outcome,
    [property: JsonPropertyName("component")] string Component,
    [property: JsonPropertyName("correlationId")] Guid CorrelationId,
    [property: JsonPropertyName("timestampUtc")] DateTimeOffset TimestampUtc,
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("action")] string? Action = null,
    [property: JsonPropertyName("operationId")] Guid? OperationId = null,
    [property: JsonPropertyName("actorReference")] string? ActorReference = null,
    [property: JsonPropertyName("resourceType")] string? ResourceType = null,
    [property: JsonPropertyName("resourceReference")] string? ResourceReference = null,
    [property: JsonPropertyName("problemCode")] string? ProblemCode = null,
    [property: JsonPropertyName("durationMs")] long? DurationMs = null);
