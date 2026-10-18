using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes;

public sealed record RuntimeStoredRevision(Guid RevisionId, RuntimeRelease Release, string MeasuredVersion,
    string ConfigurationIdentity, long CatalogRevision, long TrustRevision);
public sealed record RuntimeServiceSnapshot(bool Running, bool BootEnabled, string? ConfigurationIdentity, Guid? ConfigurationSnapshotId,
    Guid? OwnedResourcesSnapshotId);
public sealed record RuntimePreparedSwitch(RuntimeStoredRevision Revision, bool RunAfterSwitch, bool BootEnabledAfterSwitch);
public sealed record RuntimeActivationEvidence(string MeasuredVersion, string ConfigurationIdentity,
    bool NativeConfigurationValid, bool Healthy, bool Running, bool BootEnabled);
public sealed record RuntimeRestorationEvidence(string? MeasuredVersion, string? ConfigurationIdentity,
    bool Running, bool BootEnabled, bool OwnedResourcesRestored, bool Healthy);

/// <summary>Helper receipt for an existing installation operation, not a second user-facing operation ledger.</summary>
public sealed record RuntimeSwitchReceipt(Guid OperationId, string RequestHash, string IdempotencyHash,
    RuntimeSwitchOutcome Outcome, Guid? ActiveRevisionId, string? ProblemCode, DateTimeOffset CompletedAt,
    RuntimeServiceSnapshot? RetainedSnapshot);
public sealed record RuntimePendingSwitch(Guid OperationId, string RequestHash, string IdempotencyHash,
    RuntimeChangePlan Plan, InstallationStage Stage, RuntimeStoredRevision? Previous,
    RuntimeServiceSnapshot? Snapshot, RuntimePreparedSwitch? Prepared, bool SwitchStarted, string? ProblemCode,
    string? RecoveryProblemCode);
public sealed record RuntimeArchiveState(RuntimeInstallation Installation, RuntimeStoredRevision? Active, string? ConfigurationIdentity,
    RuntimeStoredRevision[] Revisions, RuntimePendingSwitch? Pending, RuntimeSwitchReceipt[] Receipts,
    RuntimePendingRemoval? Removal = null);

/// <summary>
/// Closed runtime-specific implementation, never supplied by a client. Backups and preparation must be
/// durable and identifiable by operationId before returning, including partially completed preparation.
/// Configuration checks use published state and current credential authorization, never an unrelated draft.
/// </summary>
public interface IRuntimeArchiveSwitchAdapter
{
    Task PreflightAsync(RuntimeChangePlan plan, RuntimeArchiveState current, CancellationToken ct);
    Task<RuntimeServiceSnapshot> CaptureAsync(Guid operationId, RuntimeArchiveState current, CancellationToken ct);
    Task<RuntimePreparedSwitch> PrepareAsync(Guid operationId, RuntimeChangePlan plan, RuntimeServiceSnapshot before, CancellationToken ct);
    Task ValidateSwitchAsync(RuntimePendingSwitch pending, CancellationToken ct);
    Task ActivateAsync(RuntimePendingSwitch pending, CancellationToken ct);
    Task<RuntimeActivationEvidence> VerifyAsync(RuntimePendingSwitch pending, CancellationToken ct);
    Task ValidateRestoreAsync(RuntimePendingSwitch pending, CancellationToken ct);
    Task StopForRecoveryAsync(RuntimePendingSwitch pending, CancellationToken ct);
    Task<RuntimeRestorationEvidence> RestoreAsync(RuntimePendingSwitch pending, CancellationToken ct);
    Task CleanupPreparationAsync(RuntimePendingSwitch pending, CancellationToken ct);
}
