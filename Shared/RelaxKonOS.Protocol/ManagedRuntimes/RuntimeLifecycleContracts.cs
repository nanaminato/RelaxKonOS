using RelaxKonOS.Protocol.Installations;
using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.ManagedRuntimes;

/// <summary>
/// One program revision the host still stores. Only these are eligible rollback targets, and the
/// immutable release identity is reported rather than inferred from a version label.
/// </summary>
public sealed record RuntimeInstalledRelease(Guid RevisionId, string ReleaseId, string MeasuredVersion);

/// <summary>Public immutable observation. Program and current published configuration identities are distinct.</summary>
public sealed record RuntimeInstallationSnapshot(RuntimeInstallation Installation, string? MeasuredVersion,
    Guid? ActiveRevisionId, string? ConfigurationIdentity, Guid? PendingOperationId,
    InstallationStage? PendingStage, string? ProblemCode, RuntimeSwitchResult? Receipt = null,
    IReadOnlyList<RuntimeInstalledRelease>? InstalledReleases = null);

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeSwitchOutcome>))]
public enum RuntimeSwitchOutcome { Succeeded, Failed, Cancelled, Interrupted, RecoveryRequired }
public sealed record RuntimeSwitchResult(Guid OperationId, RuntimeSwitchOutcome Outcome, string? ProblemCode);

/// <summary>Helper-local request. The ephemeral authorization channel never belongs in public options or receipts.</summary>
/// <param name="Confirmed">Operator acknowledgement of an irreversible close; no other lifecycle verb sets it.</param>
/// <param name="CancelOperationId">The running transaction a cancellation request is addressed to; only
/// <see cref="Privileged.PrivilegedOperationKind.SingBoxRuntimeCancel"/> sets it.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeLifecycleRequest(string? SourceId = null, RuntimePlanRequest? Plan = null,
    RuntimeAuthorizationChannel? Authorization = null, RuntimeChangePlan? FrozenPlan = null, Guid? RecoveryOperationId = null,
    bool Confirmed = false, Guid? CancelOperationId = null);

/// <summary>
/// Whether a cancellation request was recorded for one transaction. It says nothing about the outcome:
/// a request that arrives after the durable switch checkpoint is still recorded but no longer honored,
/// and the operation's own receipt remains the only authority on what happened.
/// </summary>
public sealed record RuntimeCancelRequestResult(Guid OperationId, bool Requested);

/// <summary>A removal names no release and may name no source; every other plan must name both.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimePlanSelection(string? SourceId, RuntimePlanRequest Request);

/// <summary>
/// What the fixed platform host reports right now. Never inferred from a version string or a running
/// process name, and never a substitute for the transaction ledger.
/// </summary>
public sealed record RuntimeServiceObservation(bool Exists, bool Running, bool? BootEnabled);

/// <summary>
/// Ledger facts about the one unfinished transaction plus the host's live service observation. It
/// carries no path, command, credential, or configuration text, so it is safe to render and to log.
/// </summary>
public sealed record RuntimeRecoverySnapshot(bool Required, Guid? OperationId, RuntimeChange? Change,
    InstallationStage? Stage, bool SwitchStarted, string? ProblemCode, string? RecoveryProblemCode,
    string? TargetReleaseId, string? PreviousReleaseId, RuntimeServiceObservation? Service = null,
    string? ServiceProblem = null)
{
    /// <summary>True only when the Host has recorded a failed compensation, not merely an unfinished stage.</summary>
    public bool NeedsOperatorDecision => Required && RecoveryProblemCode is not null;
}

/// <summary>The operator names the exact transaction it is recovering and acknowledges the consequence.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeRecoveryActionRequest(Guid OperationId, bool Confirmed = false);
