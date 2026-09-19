using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.ApplicationDeployments;

/// <summary>
/// A durable deployment operation. Long operations answer <c>202 Accepted</c> with this DTO; the
/// durable record, not a real-time event, is the authoritative source for state.
/// </summary>
/// <param name="Progress">Verified in-stage bytes or work units. Null means no reliable denominator
/// exists yet, and the UI must present unknown progress rather than a fabricated percentage.</param>
/// <param name="RecoveryProblemCode">Set only when a failure left resources that need operator
/// attention. It never replaces the original <paramref name="ProblemCode"/>.</param>
public sealed record DeploymentOperationDto(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("applicationId")] Guid ApplicationId,
    [property: JsonPropertyName("applicationName")] string ApplicationName,
    [property: JsonPropertyName("kind")] DeploymentOperationKind Kind,
    [property: JsonPropertyName("targetRevisionId")] Guid? TargetRevisionId,
    [property: JsonPropertyName("targetRevisionNumber")] int? TargetRevisionNumber,
    [property: JsonPropertyName("state")] DeploymentOperationState State,
    [property: JsonPropertyName("stage")] DeploymentStage Stage,
    [property: JsonPropertyName("progress")] int? Progress,
    [property: JsonPropertyName("problemCode")] string? ProblemCode,
    [property: JsonPropertyName("recoveryProblemCode")] string? RecoveryProblemCode,
    [property: JsonPropertyName("requestedByReference")] string RequestedByReference,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("startedAt")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("completedAt")] DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("cancellable")] bool Cancellable);

/// <summary>Aggregate, drift-aware view of one deployment target. It carries no secret material.</summary>
public sealed record ApplicationDeploymentSnapshotDto(
    [property: JsonPropertyName("application")] ApplicationDto Application,
    [property: JsonPropertyName("revisions")] IReadOnlyList<ApplicationRevisionDto> Revisions,
    [property: JsonPropertyName("operations")] IReadOnlyList<DeploymentOperationDto> Operations,
    [property: JsonPropertyName("activeOperation")] DeploymentOperationDto? ActiveOperation = null);
