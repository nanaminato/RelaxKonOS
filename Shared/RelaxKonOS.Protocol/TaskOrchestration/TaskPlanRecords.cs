using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.TaskOrchestration;

[JsonConverter(typeof(JsonStringEnumConverter<TaskPlanValidity>))]
public enum TaskPlanValidity { Resolved, Invalidated }

/// <summary>Plan bytes are immutable; validity, expiry and their revision are separate records.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskPlanRecord(TaskPlan Plan, TaskPlanValidity Validity, long Revision,
    DateTimeOffset ExpiresAt, DateTimeOffset? InvalidatedAt, string? ProblemCode);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvalidateTaskPlanRequest(string PlanDigest, long ExpectedRevision);
