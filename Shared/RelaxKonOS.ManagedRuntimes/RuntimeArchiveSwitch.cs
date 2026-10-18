using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes;

/// <summary>
/// Helper-side archive switch transaction. The caller supplies the Server installation operation ID.
/// The instance lock is also required for configuration publication and other lifecycle verbs.
/// Package transactions and data-removing uninstalls must use their own adapters, never this switch.
/// </summary>
public sealed class RuntimeArchiveSwitch(RuntimeArchiveStateStore store, IRuntimeArchiveSwitchAdapter adapter,
    Func<InstallationStage, Task>? progress = null)
{
    public async Task<RuntimeSwitchResult> ExecuteAsync(Guid operationId, RuntimeChangePlan plan, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new RuntimePolicyException("runtime.operation_id_invalid");
        var requestHash = Hash(RuntimeJson.Serialize(plan));
        var key = plan.Request.IdempotencyKey;
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(c => c < 33 || c > 126))
            throw new RuntimePolicyException("runtime.idempotency_key_invalid");
        var keyHash = Hash(Encoding.UTF8.GetBytes(plan.Request.HostId + "\n" + plan.Request.InstallationId + "\n" + key));
        using var session = store.Acquire();
        var current = session.State ?? throw new RuntimePolicyException("runtime.installation_observation_required");
        var receipt = current.Receipts.FirstOrDefault(r => r.OperationId == operationId || r.IdempotencyHash == keyHash);
        if (receipt is not null)
        {
            if (receipt.RequestHash != requestHash || receipt.IdempotencyHash != keyHash)
                throw new RuntimePolicyException("runtime.idempotency_conflict");
            return new(receipt.OperationId, receipt.Outcome, receipt.ProblemCode);
        }
        if (current.Pending is { } pendingRequest && (pendingRequest.OperationId == operationId || pendingRequest.IdempotencyHash == keyHash)
            && (pendingRequest.RequestHash != requestHash || pendingRequest.IdempotencyHash != keyHash))
            throw new RuntimePolicyException("runtime.idempotency_conflict");
        if (current.Pending is not null || current.Installation.RecoveryRequired)
            throw new RuntimePolicyException("runtime.recovery_required");
        ValidatePlan(plan, current);
        ct.ThrowIfCancellationRequested();
        Save(session, current with
        {
            Pending = new(operationId, requestHash, keyHash, plan, InstallationStage.Preflight,
                current.Active, null, null, false, null, null)
        });
        try
        {
            await NotifyAsync(InstallationStage.Preflight);
            await adapter.PreflightAsync(plan, session.State!, ct);
            var snapshot = await adapter.CaptureAsync(operationId, session.State!, ct);
            if (snapshot.ConfigurationIdentity != session.State!.ConfigurationIdentity)
                throw new RuntimePolicyException("runtime.configuration_drift");
            if ((snapshot.ConfigurationIdentity is null) != (snapshot.ConfigurationSnapshotId is null)
                || snapshot.ConfigurationIdentity is { } identity && (identity.Length != 64 || !identity.All(Uri.IsHexDigit))
                || plan.Request.Change == RuntimeChange.Install && (snapshot.Running || snapshot.BootEnabled))
                throw new RuntimePolicyException("runtime.snapshot_invalid");
            UpdatePending(session, p => p with { Snapshot = snapshot, Stage = InstallationStage.Preparing });
            await NotifyAsync(InstallationStage.Preparing);
            var prepared = await adapter.PrepareAsync(operationId, plan, snapshot, ct);
            ValidatePrepared(plan, snapshot, prepared, session.State!);
            Save(session, session.State! with
            {
                Revisions = [.. session.State!.Revisions, prepared.Revision],
                Pending = session.State.Pending! with { Prepared = prepared, Stage = InstallationStage.Verifying }
            });
            await NotifyAsync(InstallationStage.Verifying);
            await adapter.ValidateSwitchAsync(session.State!.Pending!, ct);
            ct.ThrowIfCancellationRequested();
            // This durable checkpoint is the cancellation boundary. Active pointer and journal change together.
            Save(session, session.State! with
            {
                Active = prepared.Revision,
                ConfigurationIdentity = prepared.Revision.ConfigurationIdentity,
                Installation = session.State.Installation with
                { Ownership = RuntimeOwnership.Managed, ActiveReleaseId = prepared.Revision.Release.ReleaseId },
                Pending = session.State.Pending! with { SwitchStarted = true, Stage = InstallationStage.Activating }
            });
            await NotifyAsync(InstallationStage.Activating);
            await adapter.ActivateAsync(session.State!.Pending!, CancellationToken.None);
            UpdatePending(session, p => p with { Stage = InstallationStage.HealthChecking });
            await NotifyAsync(InstallationStage.HealthChecking);
            var evidence = await adapter.VerifyAsync(session.State!.Pending!, CancellationToken.None);
            if (!Matches(prepared, evidence)) throw new RuntimePolicyException("runtime.health_check_failed");
            // Backups remain identified by the receipt. Retention/deletion is an owned-resource operation.
            return Complete(session, RuntimeSwitchOutcome.Succeeded, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            var cancelled = ex is OperationCanceledException && ct.IsCancellationRequested && !session.State!.Pending!.SwitchStarted;
            var problem = cancelled ? "runtime.operation_cancelled"
                : ex is RuntimePolicyException policy ? policy.ProblemCode : "runtime.switch_failed";
            return await CompensateAsync(session, problem, cancelled ? RuntimeSwitchOutcome.Cancelled : RuntimeSwitchOutcome.Failed);
        }
    }

    /// <summary>Startup reconciles a persisted operation; it never re-runs preparation or creates new credentials.</summary>
    public async Task<RuntimeSwitchResult?> RecoverAsync(Guid operationId, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new RuntimePolicyException("runtime.operation_id_invalid");
        using var session = store.Acquire();
        if (session.State?.Pending is not { } pending) return null;
        if (pending.OperationId != operationId) throw new RuntimePolicyException("runtime.recovery_target_conflict");
        ct.ThrowIfCancellationRequested();
        return await CompensateAsync(session, pending.ProblemCode ?? "runtime.operation_interrupted", RuntimeSwitchOutcome.Interrupted);
    }

    private async Task<RuntimeSwitchResult> CompensateAsync(RuntimeArchiveStateStore.Session session, string problem,
        RuntimeSwitchOutcome outcome)
    {
        try
        {
            UpdatePending(session, p => p with { Stage = InstallationStage.RollingBack, ProblemCode = problem });
            await NotifyAsync(InstallationStage.RollingBack);
            var pending = session.State!.Pending!;
            if (pending.SwitchStarted)
            {
                // Stop first: invalid/revoked old configuration must never be restarted by compensation.
                await adapter.StopForRecoveryAsync(pending, CancellationToken.None);
                await adapter.ValidateRestoreAsync(pending, CancellationToken.None);
                Save(session, session.State! with
                {
                    Active = pending.Previous,
                    ConfigurationIdentity = pending.Snapshot!.ConfigurationIdentity,
                    Installation = session.State.Installation with
                    {
                        ActiveReleaseId = pending.Previous?.Release.ReleaseId,
                        Ownership = pending.Previous is null ? RuntimeOwnership.Absent : RuntimeOwnership.Managed
                    }
                });
                var restored = await adapter.RestoreAsync(session.State!.Pending!, CancellationToken.None);
                var snapshot = pending.Snapshot!;
                if (!restored.Healthy || !restored.OwnedResourcesRestored || restored.MeasuredVersion != pending.Previous?.MeasuredVersion
                    || restored.ConfigurationIdentity != snapshot.ConfigurationIdentity || restored.Running != snapshot.Running
                    || restored.BootEnabled != snapshot.BootEnabled)
                    throw new RuntimePolicyException("runtime.compensation_health_failed");
            }
            await adapter.CleanupPreparationAsync(session.State!.Pending!, CancellationToken.None);
            // Rejected candidate directories are adapter-owned cleanup; never offer them as rollback revisions.
            return Complete(session, outcome, problem);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // If the disk itself fails, the previously persisted nonterminal checkpoint still blocks new work.
            try
            {
                Save(session, session.State! with
                {
                    Installation = session.State!.Installation with { RecoveryRequired = true },
                    Pending = session.State.Pending! with { Stage = InstallationStage.Interrupted, ProblemCode = problem,
                        RecoveryProblemCode = ex is RuntimePolicyException policy ? policy.ProblemCode : "runtime.compensation_failed" }
                });
            }
            catch (Exception saveError) when (saveError is IOException or UnauthorizedAccessException or RuntimePolicyException) { }
            return new(session.State!.Pending!.OperationId, RuntimeSwitchOutcome.RecoveryRequired, "runtime.recovery_required");
        }
    }

    private async Task NotifyAsync(InstallationStage stage)
    {
        if (progress is null) return;
        try { await progress(stage); }
        catch (IOException) { /* Losing a progress reader does not cancel an accepted root transaction. */ }
        catch (ObjectDisposedException) { }
    }

    private static RuntimeSwitchResult Complete(RuntimeArchiveStateStore.Session session, RuntimeSwitchOutcome outcome, string? problem)
    {
        var state = session.State!;
        var pending = state.Pending!;
        var receipt = new RuntimeSwitchReceipt(pending.OperationId, pending.RequestHash, pending.IdempotencyHash,
            outcome, state.Active?.RevisionId, problem, DateTimeOffset.UtcNow, pending.Snapshot);
        Save(session, state with
        {
            Installation = state.Installation with { RecoveryRequired = false },
            Pending = null, Receipts = [.. state.Receipts, receipt],
            Revisions = outcome == RuntimeSwitchOutcome.Succeeded ? state.Revisions
                : state.Revisions.Where(r => r.RevisionId != pending.Prepared?.Revision.RevisionId).ToArray()
        });
        return new(receipt.OperationId, receipt.Outcome, receipt.ProblemCode);
    }

    private static void UpdatePending(RuntimeArchiveStateStore.Session session, Func<RuntimePendingSwitch, RuntimePendingSwitch> update) =>
        Save(session, session.State! with { Pending = update(session.State!.Pending!) });

    private static void Save(RuntimeArchiveStateStore.Session session, RuntimeArchiveState state) =>
        session.Save(state with { Installation = state.Installation with { StateRevision = checked(session.State!.Installation.StateRevision + 1) } });

    private static void ValidatePlan(RuntimeChangePlan plan, RuntimeArchiveState state)
    {
        var request = plan.Request;
        var installation = state.Installation;
        if (state.Revisions.Length >= 256 || state.Receipts.Length >= 4096)
            throw new RuntimePolicyException("runtime.transaction_state_limit");
        if (request.Change is not (RuntimeChange.Install or RuntimeChange.Update or RuntimeChange.Repair or RuntimeChange.Rollback))
            throw new RuntimePolicyException("runtime.action_unsupported");
        if (request.InstallationId != installation.InstallationId || request.HostId != installation.HostId || plan.RuntimeId != installation.RuntimeId)
            throw new RuntimePolicyException("runtime.installation_mismatch");
        if (request.ExpectedStateRevision != installation.StateRevision) throw new RuntimePolicyException("runtime.state_revision_conflict");
        if (installation.Ownership == RuntimeOwnership.Unknown) throw new RuntimePolicyException("runtime.state_unknown");
        if (installation.Ownership == RuntimeOwnership.External) throw new RuntimePolicyException("runtime.external_installation");
        if (request.Change == RuntimeChange.Install && installation.Ownership != RuntimeOwnership.Absent)
            throw new RuntimePolicyException("runtime.already_installed");
        if (request.Change != RuntimeChange.Install && installation.Ownership != RuntimeOwnership.Managed)
            throw new RuntimePolicyException("runtime.not_managed");
        if (plan.PreviousReleaseId != state.Active?.Release.ReleaseId) throw new RuntimePolicyException("runtime.state_revision_conflict");
        if (plan.Release is not { } release || release.ReleaseId != request.ReleaseId || release.RuntimeId != installation.RuntimeId
            || release.Target != installation.Target || release.Trust != RuntimeTrust.Confirmed
            || !release.ConfigurationSupported || !RuntimeArchiveRules.Valid(release.Artifact)
            || request.ExpectedCatalogRevision < 1 || request.ExpectedTrustRevision < 1)
            throw new RuntimePolicyException("runtime.release_untrusted");
        if (release.Channel == RuntimeChannel.Prerelease && !request.AllowPrerelease)
            throw new RuntimePolicyException("runtime.channel_not_allowed");
        if (request.Change == RuntimeChange.Repair && release.ReleaseId != state.Active?.Release.ReleaseId)
            throw new RuntimePolicyException("runtime.repair_target_mismatch");
        if (request.Change is RuntimeChange.Update or RuntimeChange.Rollback && release.ReleaseId == state.Active?.Release.ReleaseId)
            throw new RuntimePolicyException("runtime.target_unchanged");
        if (request.Change == RuntimeChange.Update && release.OriginalVersion == state.Active?.Release.OriginalVersion)
            throw new RuntimePolicyException("runtime.version_unchanged");
        if (request.Change == RuntimeChange.Rollback && !state.Revisions.Any(r => r.Release.ReleaseId == release.ReleaseId
            && r.Release.Artifact.Sha256.Equals(release.Artifact.Sha256, StringComparison.OrdinalIgnoreCase)))
            throw new RuntimePolicyException("runtime.rollback_unavailable");
    }

    private static void ValidatePrepared(RuntimeChangePlan plan, RuntimeServiceSnapshot snapshot, RuntimePreparedSwitch prepared, RuntimeArchiveState state)
    {
        var revision = prepared.Revision;
        if (revision.RevisionId != state.Pending!.OperationId || state.Revisions.Any(r => r.RevisionId == revision.RevisionId)
            || !RuntimeJson.Serialize(revision.Release).AsSpan().SequenceEqual(RuntimeJson.Serialize(plan.Release))
            || revision.CatalogRevision != plan.Request.ExpectedCatalogRevision || revision.TrustRevision != plan.Request.ExpectedTrustRevision
            || string.IsNullOrWhiteSpace(revision.MeasuredVersion) || revision.ConfigurationIdentity.Length != 64
            || !revision.ConfigurationIdentity.All(Uri.IsHexDigit)
            || (plan.Request.Change == RuntimeChange.Install
                ? prepared.RunAfterSwitch || prepared.BootEnabledAfterSwitch
                : prepared.RunAfterSwitch != snapshot.Running || prepared.BootEnabledAfterSwitch != snapshot.BootEnabled))
            throw new RuntimePolicyException("runtime.prepared_identity_invalid");
    }

    private static bool Matches(RuntimePreparedSwitch prepared, RuntimeActivationEvidence evidence) =>
        evidence.NativeConfigurationValid && evidence.Healthy
        && evidence.MeasuredVersion == prepared.Revision.MeasuredVersion
        && evidence.ConfigurationIdentity == prepared.Revision.ConfigurationIdentity
        && evidence.Running == prepared.RunAfterSwitch && evidence.BootEnabled == prepared.BootEnabledAfterSwitch;

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
