using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.Protocol.TaskOrchestration;

[JsonConverter(typeof(JsonStringEnumConverter<TaskRecoveryAction>))]
public enum TaskRecoveryAction { RetryCompensation, EmergencyStop, Close }

// Credentials are request-only; no generated ToString and no durable credential fields.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TaskRecoveryRequest
{
    [JsonRequired] public long ExpectedRevision { get; init; }
    [JsonRequired] public Guid OperationId { get; init; }
    [JsonRequired] public TaskRecoveryAction Action { get; init; }
    [JsonRequired] public bool Confirmed { get; init; }
    [JsonRequired] public string? AdministratorUsername { get; init; }
    [JsonRequired] public string? Password { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskRecoveryAttempt(Guid AttemptId, Guid RunId, string StepId, Guid OperationId,
    long ExpectedRevision, TaskRecoveryAction Action, string CanonicalSubject, string SessionIdentity, string HostId,
    string PolicyRevision, string ApproverIdentity, string AuthenticationMethod, DateTimeOffset CreatedAt,
    DateTimeOffset ValidUntil, bool ResponseRecorded, string? ProblemCode);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskRecoveryView(TaskRun Run, RuntimeRecoverySnapshot Recovery, ImmutableArray<TaskRecoveryAttempt> Attempts);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskRecoveryResponse(TaskRun Run, TaskRecoveryAttempt Attempt);
