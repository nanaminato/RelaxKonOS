using RelaxKonOS.Protocol.Installations;
using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.ManagedRuntimes;

/// <summary>Public immutable observation. Program and current published configuration identities are distinct.</summary>
public sealed record RuntimeInstallationSnapshot(RuntimeInstallation Installation, string? MeasuredVersion,
    Guid? ActiveRevisionId, string? ConfigurationIdentity, Guid? PendingOperationId,
    InstallationStage? PendingStage, string? ProblemCode, RuntimeSwitchResult? Receipt = null);

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeSwitchOutcome>))]
public enum RuntimeSwitchOutcome { Succeeded, Failed, Cancelled, Interrupted, RecoveryRequired }
public sealed record RuntimeSwitchResult(Guid OperationId, RuntimeSwitchOutcome Outcome, string? ProblemCode);

/// <summary>Helper-local request. The ephemeral authorization channel never belongs in public options or receipts.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeLifecycleRequest(string? SourceId = null, RuntimePlanRequest? Plan = null,
    RuntimeAuthorizationChannel? Authorization = null, RuntimeChangePlan? FrozenPlan = null, Guid? RecoveryOperationId = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimePlanSelection(string SourceId, RuntimePlanRequest Request);
