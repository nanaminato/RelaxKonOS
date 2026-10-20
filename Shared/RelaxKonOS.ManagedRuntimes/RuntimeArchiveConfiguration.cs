using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes;

public sealed record RuntimePendingConfiguration(Guid OperationId, string RequestHash, string IdempotencyHash,
    RuntimeConfigurationPlan Plan, InstallationStage Stage, RuntimeServiceSnapshot? Snapshot,
    bool SwitchStarted, bool PreviousRestored, string? ProblemCode, string? RecoveryProblemCode);

/// <summary>
/// Preparation must leave live configuration and data untouched. Activation must stop the owned service,
/// durably snapshot its quiescent mutable data, then replace configuration and preserve service/boot state.
/// Restore must tolerate interruption at every point, including a partly copied data backup. Authorization
/// for candidate and old configuration is read afresh by the adapter, including immediately before start.
/// </summary>
public interface IRuntimeArchiveConfigurationAdapter
{
    Task PreflightAsync(RuntimeConfigurationPlan plan, RuntimeArchiveState current, CancellationToken ct);
    Task<RuntimeServiceSnapshot> CaptureAsync(Guid operationId, RuntimeArchiveState current, CancellationToken ct);
    Task PrepareAsync(RuntimePendingConfiguration pending, CancellationToken ct);
    Task ValidateSwitchAsync(RuntimePendingConfiguration pending, CancellationToken ct);
    Task ActivateAsync(RuntimePendingConfiguration pending, CancellationToken ct);
    Task<RuntimeActivationEvidence> VerifyAsync(RuntimePendingConfiguration pending, CancellationToken ct);
    Task StopForRecoveryAsync(RuntimePendingConfiguration pending, CancellationToken ct);
    Task ValidateRestoreAsync(RuntimePendingConfiguration pending, CancellationToken ct);
    Task<RuntimeRestorationEvidence> RestoreAsync(RuntimePendingConfiguration pending, CancellationToken ct);
    /// <summary>Idempotently removes transient preparation only. Durable old configuration/data snapshots
    /// remain available for recovery and are retained by the terminal receipt; cleanup must not delete them.</summary>
    Task CleanupPreparationAsync(RuntimePendingConfiguration pending, CancellationToken ct);
}

/// <summary>
/// Configuration-only durable transaction sharing the archive instance lock and receipts. Program revisions
/// never change. Recovery only compensates; it cannot regenerate a candidate or silently complete a publish.
/// </summary>
public sealed class RuntimeArchiveConfiguration(RuntimeArchiveStateStore store,
    IRuntimeArchiveConfigurationAdapter adapter, RuntimeStageReporter? progress = null)
{
    public async Task<RuntimeSwitchResult> ExecuteAsync(Guid operationId, RuntimeConfigurationPlan plan, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new RuntimePolicyException("runtime.operation_id_invalid");
        if (string.IsNullOrWhiteSpace(plan.IdempotencyKey) || plan.IdempotencyKey.Length > 128
            || plan.IdempotencyKey.Any(c => c < 33 || c > 126))
            throw new RuntimePolicyException("runtime.idempotency_key_invalid");
        var requestHash = Hash(RuntimeJson.Serialize(plan));
        var keyHash = Hash(Encoding.UTF8.GetBytes(plan.HostId + "\n" + plan.InstallationId + "\n" + plan.IdempotencyKey));
        using var session = store.Acquire();
        var current = session.State ?? throw new RuntimePolicyException("runtime.installation_observation_required");
        var receipt = current.Receipts.FirstOrDefault(r => r.OperationId == operationId || r.IdempotencyHash == keyHash);
        if (receipt is not null)
        {
            if (receipt.RequestHash != requestHash || receipt.IdempotencyHash != keyHash)
                throw new RuntimePolicyException("runtime.idempotency_conflict");
            return new(receipt.OperationId, receipt.Outcome, receipt.ProblemCode);
        }
        if (current.Configuration is { } pending && (pending.OperationId == operationId || pending.IdempotencyHash == keyHash)
            && (pending.RequestHash != requestHash || pending.IdempotencyHash != keyHash))
            throw new RuntimePolicyException("runtime.idempotency_conflict");
        if (current.Pending is not null || current.Removal is not null || current.Configuration is not null
            || current.Installation.RecoveryRequired) throw new RuntimePolicyException("runtime.recovery_required");
        if (plan.InstallationId != current.Installation.InstallationId || plan.HostId != current.Installation.HostId
            || plan.RuntimeId != current.Installation.RuntimeId) throw new RuntimePolicyException("runtime.installation_mismatch");
        if (current.Active is null || current.Installation.Ownership != RuntimeOwnership.Managed)
            throw new RuntimePolicyException("runtime.not_managed");
        if (plan.ExpectedStateRevision != current.Installation.StateRevision || plan.ActiveRevisionId != current.Active.RevisionId)
            throw new RuntimePolicyException("runtime.state_revision_conflict");
        if (!Digest(plan.PreviousIdentity) || !Digest(plan.CandidateIdentity))
            throw new RuntimePolicyException("runtime.configuration_invalid");
        if (plan.PreviousIdentity != current.ConfigurationIdentity) throw new RuntimePolicyException("runtime.configuration_drift");
        if (plan.PreviousIdentity == plan.CandidateIdentity) throw new RuntimePolicyException("runtime.target_unchanged");
        if (current.Receipts.Length >= 4096) throw new RuntimePolicyException("runtime.transaction_state_limit");
        ct.ThrowIfCancellationRequested();
        Save(session, current with { Configuration = new(operationId, requestHash, keyHash, plan,
            InstallationStage.Preflight, null, false, false, null, null) });
        try
        {
            await NotifyAsync(InstallationStage.Preflight, true);
            await adapter.PreflightAsync(plan, session.State!, ct);
            var snapshot = await adapter.CaptureAsync(operationId, session.State!, ct);
            if (snapshot.ConfigurationIdentity != plan.PreviousIdentity || snapshot.ConfigurationSnapshotId != operationId)
                throw new RuntimePolicyException("runtime.snapshot_invalid");
            Update(session, p => p with { Snapshot = snapshot, Stage = InstallationStage.Preparing });
            await NotifyAsync(InstallationStage.Preparing, true);
            await adapter.PrepareAsync(session.State!.Configuration!, ct);
            Update(session, p => p with { Stage = InstallationStage.Verifying });
            await NotifyAsync(InstallationStage.Verifying, true);
            await adapter.ValidateSwitchAsync(session.State!.Configuration!, ct);
            ct.ThrowIfCancellationRequested();
            Save(session, session.State! with
            {
                ConfigurationIdentity = plan.CandidateIdentity,
                Configuration = session.State.Configuration! with { SwitchStarted = true, Stage = InstallationStage.Activating }
            });
            await NotifyAsync(InstallationStage.Activating, false);
            await adapter.ActivateAsync(session.State!.Configuration!, CancellationToken.None);
            Update(session, p => p with { Stage = InstallationStage.HealthChecking });
            await NotifyAsync(InstallationStage.HealthChecking, false);
            var evidence = await adapter.VerifyAsync(session.State!.Configuration!, CancellationToken.None);
            if (!evidence.NativeConfigurationValid || !evidence.Healthy || evidence.MeasuredVersion != current.Active.MeasuredVersion
                || evidence.ConfigurationIdentity != plan.CandidateIdentity || evidence.Running != snapshot.Running
                || evidence.BootEnabled != snapshot.BootEnabled) throw new RuntimePolicyException("runtime.health_check_failed");
            return Complete(session, RuntimeSwitchOutcome.Succeeded, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            var cancelled = ex is OperationCanceledException && ct.IsCancellationRequested && !session.State!.Configuration!.SwitchStarted;
            return await CompensateAsync(session, cancelled ? "runtime.operation_cancelled"
                : ex is RuntimePolicyException policy ? policy.ProblemCode : "runtime.configuration_publish_failed",
                cancelled ? RuntimeSwitchOutcome.Cancelled : RuntimeSwitchOutcome.Failed);
        }
    }

    public async Task<RuntimeSwitchResult?> RecoverAsync(Guid operationId, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new RuntimePolicyException("runtime.operation_id_invalid");
        using var session = store.Acquire();
        if (session.State?.Configuration is not { } pending) return null;
        if (pending.OperationId != operationId) throw new RuntimePolicyException("runtime.recovery_target_conflict");
        ct.ThrowIfCancellationRequested();
        return await CompensateAsync(session, pending.ProblemCode ?? "runtime.operation_interrupted", RuntimeSwitchOutcome.Interrupted);
    }

    private async Task<RuntimeSwitchResult> CompensateAsync(RuntimeArchiveStateStore.Session session, string problem, RuntimeSwitchOutcome outcome)
    {
        try
        {
            Update(session, p => p with { Stage = InstallationStage.RollingBack, ProblemCode = problem });
            await NotifyAsync(InstallationStage.RollingBack, false);
            var pending = session.State!.Configuration!;
            if (pending.SwitchStarted)
            {
                await adapter.StopForRecoveryAsync(pending, CancellationToken.None);
                await adapter.ValidateRestoreAsync(pending, CancellationToken.None);
                // Keep the candidate identity until restoration has actually been verified. A crash after
                // restore simply replays the idempotent restoration rather than claiming it already worked.
                var restored = await adapter.RestoreAsync(pending, CancellationToken.None);
                var before = pending.Snapshot!;
                if (!restored.Healthy || !restored.OwnedResourcesRestored || restored.MeasuredVersion != session.State.Active!.MeasuredVersion
                    || restored.ConfigurationIdentity != pending.Plan.PreviousIdentity || restored.Running != before.Running
                    || restored.BootEnabled != before.BootEnabled) throw new RuntimePolicyException("runtime.compensation_health_failed");
                Save(session, session.State with { ConfigurationIdentity = pending.Plan.PreviousIdentity,
                    Configuration = pending with { PreviousRestored = true } });
            }
            await adapter.CleanupPreparationAsync(session.State!.Configuration!, CancellationToken.None);
            return Complete(session, outcome, problem);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            try
            {
                Save(session, session.State! with
                {
                    Installation = session.State!.Installation with { RecoveryRequired = true },
                    Configuration = session.State.Configuration! with { Stage = InstallationStage.Interrupted,
                        ProblemCode = problem, RecoveryProblemCode = ex is RuntimePolicyException policy
                            ? policy.ProblemCode : "runtime.compensation_failed" }
                });
            }
            catch (Exception saveError) when (saveError is IOException or UnauthorizedAccessException or RuntimePolicyException) { }
            return new(session.State!.Configuration!.OperationId, RuntimeSwitchOutcome.RecoveryRequired, "runtime.recovery_required");
        }
    }

    private async Task NotifyAsync(InstallationStage stage, bool cancellable)
    {
        if (progress is null) return;
        try { await progress(stage, cancellable); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static RuntimeSwitchResult Complete(RuntimeArchiveStateStore.Session session, RuntimeSwitchOutcome outcome, string? problem)
    {
        var state = session.State!;
        var pending = state.Configuration!;
        var receipt = new RuntimeSwitchReceipt(pending.OperationId, pending.RequestHash, pending.IdempotencyHash,
            outcome, state.Active!.RevisionId, problem, DateTimeOffset.UtcNow, pending.Snapshot);
        Save(session, state with { Configuration = null, Installation = state.Installation with { RecoveryRequired = false },
            Receipts = [.. state.Receipts, receipt] });
        return new(receipt.OperationId, receipt.Outcome, receipt.ProblemCode);
    }

    private static void Update(RuntimeArchiveStateStore.Session session, Func<RuntimePendingConfiguration, RuntimePendingConfiguration> update) =>
        Save(session, session.State! with { Configuration = update(session.State!.Configuration!) });
    private static void Save(RuntimeArchiveStateStore.Session session, RuntimeArchiveState state) => session.Save(state with
        { Installation = state.Installation with { StateRevision = checked(session.State!.Installation.StateRevision + 1) } });
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
