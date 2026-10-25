using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.TaskOrchestration;

[JsonConverter(typeof(JsonStringEnumConverter<TaskApprovalState>))]
public enum TaskApprovalState { Granted, Consumed, Expired, Revoked }
[JsonConverter(typeof(JsonStringEnumConverter<TaskRunState>))]
public enum TaskRunState { Queued, Running, Cancelling, Compensating, NeedsRecovery, Succeeded, Failed, Cancelled }
[JsonConverter(typeof(JsonStringEnumConverter<TaskStepRunState>))]
public enum TaskStepRunState { Pending, Dispatching, Running, Reconciling, Succeeded, NoOp, Skipped, Failed, Cancelled, Compensating, Compensated, NeedsRecovery }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskPlanApproval(Guid ApprovalId, Guid PlanId, string PlanDigest, TaskApprovalState State,
    long Revision, string CanonicalSubject, string SessionIdentity, string HostId, string ApproverIdentity,
    string AuthenticationMethod, string PolicyRevision, DateTimeOffset CreatedAt, DateTimeOffset StartBefore,
    DateTimeOffset RunAuthorizationExpiresAt, Guid? RunId, ImmutableArray<TaskCapabilityTarget> Capabilities,
    DateTimeOffset? RevokedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskStepRun(string StepId, TaskStepRunState State, string IdempotencyKey,
    string? RequestDigest, Guid? DomainOperationId, string? ProblemCode);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskRun(Guid RunId, Guid PlanId, string PlanDigest, Guid ApprovalId, string CanonicalSubject,
    string SessionIdentity, string HostId, string PolicyRevision, TaskRunState State, long Revision,
    bool CancellationRequested, DateTimeOffset CreatedAt, ImmutableArray<TaskStepRun> Steps, string? ProblemCode);
