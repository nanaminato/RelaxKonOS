using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.TaskOrchestration;

[JsonConverter(typeof(JsonStringEnumConverter<TaskStepDisposition>))]
public enum TaskStepDisposition { Ready, NoOp, Conflict, Unsupported }

/// <summary>Intent is not a plan or an execution authorization.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskStepIntent(string StepId, string ActionId, int SchemaVersion,
    string ImplementationRevision, JsonElement Inputs, ImmutableArray<string> DependsOn);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskPlanIntent(string SourceKind, string SourceIdentity, long InputRevision,
    ImmutableArray<TaskStepIntent> Steps);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskResourceBaseline(string ResourceKey, string Identity, long Revision);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskCapabilityTarget(string Capability, string Target);

/// <summary>All lists are immutable. Inputs are cloned before freezing by the trusted planner.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskStep(string StepId, string ActionId, int SchemaVersion, string ImplementationRevision,
    JsonElement Inputs, ImmutableArray<string> DependsOn, ImmutableArray<string> ResourceKeys,
    ImmutableArray<TaskResourceBaseline> Baselines, ImmutableArray<TaskCapabilityTarget> RequiredCapabilities,
    TaskStepDisposition Disposition, ImmutableArray<string> Problems, string RiskClass,
    string RetryPolicy, string CompensationKind);

/// <summary>Explicit bounded V1 limits. Changing them changes plan semantics.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskPlanLimits(int MaximumSteps, int MaximumDependencyDepth, int MaximumPlanBytes,
    int MaximumInputBytes, int MaximumParallelism, int MaximumRetries, int MaximumRunSeconds)
{
    public static TaskPlanLimits Default { get; } = new(64, 16, 262144, 16384, 1, 0, 600);
}

/// <summary>Digest payload; envelope identity, time and translated presentation never enter this value.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskPlanSemantics(string HostId, string CanonicalSubject, string SourceKind,
    string SourceIdentity, long InputRevision, ImmutableArray<TaskStep> Steps, string FailurePolicy,
    string CancellationPolicy, bool ContinueWhenDisconnected, TaskPlanLimits Limits);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskPlan(Guid PlanId, DateTimeOffset CreatedAt, string PlanDigest, TaskPlanSemantics Semantics);

public sealed class TaskPlanningException(string problemCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
}
