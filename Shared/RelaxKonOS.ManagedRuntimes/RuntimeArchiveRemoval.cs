using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes;

/// <summary>
/// Durable checkpoint for removing one managed archive installation. Service removal and irreversible
/// cleanup are separate boundaries: a failure before cleanup can restore the exact previous registration,
/// while a failure after it is reported instead of pretending the original state returned.
/// </summary>
public sealed record RuntimePendingRemoval(Guid OperationId, string RequestHash, string IdempotencyHash,
    RuntimeChangePlan Plan, InstallationStage Stage, RuntimeServiceSnapshot? Snapshot, bool RemovalStarted,
    bool CleanupStarted, string? ProblemCode, string? RecoveryProblemCode);

/// <summary>
/// Closed removal adapter. It may only touch resources this installation provably owns, never a package
/// manager, and never data outside the protected instance root.
/// </summary>
public interface IRuntimeArchiveRemovalAdapter
{
    Task PreflightAsync(RuntimeChangePlan plan, RuntimeArchiveState current, CancellationToken ct);
    Task<RuntimeServiceSnapshot> CaptureAsync(Guid operationId, RuntimeArchiveState current, CancellationToken ct);
    Task RemoveAsync(RuntimeArchiveState current, RuntimeServiceSnapshot snapshot, CancellationToken ct);
    Task<bool> VerifyRemovedAsync(CancellationToken ct);
    /// <summary>Restores the captured registration only while the recorded program and configuration still exist.</summary>
    Task<bool> RestoreAsync(RuntimeArchiveState current, RuntimeServiceSnapshot snapshot, CancellationToken ct);
    Task CleanupProgramAsync(RuntimeArchiveState current, CancellationToken ct);
    Task DeleteDataAsync(CancellationToken ct);
}

/// <summary>
/// Archive removal transaction. Reuses the Server installation operation ID and stage vocabulary so the
/// ledger stays single, and deletes data only when the request explicitly dropped the default retention.
/// </summary>
public sealed class RuntimeArchiveRemoval(RuntimeArchiveStateStore store, IRuntimeArchiveRemovalAdapter adapter,
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
        if (current.Removal is { } pending && (pending.OperationId == operationId || pending.IdempotencyHash == keyHash)
            && (pending.RequestHash != requestHash || pending.IdempotencyHash != keyHash))
            throw new RuntimePolicyException("runtime.idempotency_conflict");
        if (current.Pending is not null || current.Removal is not null || current.Installation.RecoveryRequired)
            throw new RuntimePolicyException("runtime.recovery_required");
        ValidatePlan(plan, current);
        ct.ThrowIfCancellationRequested();
        Save(session, current with
        {
            Removal = new(operationId, requestHash, keyHash, plan, InstallationStage.Preflight, null, false, false, null, null)
        });
        try
        {
            await NotifyAsync(InstallationStage.Preflight);
            await adapter.PreflightAsync(plan, session.State!, ct);
            var snapshot = await adapter.CaptureAsync(operationId, session.State!, ct);
            if ((snapshot.ConfigurationIdentity is null) != (snapshot.ConfigurationSnapshotId is null)
                || snapshot.ConfigurationIdentity is { } identity && (identity.Length != 64 || !identity.All(Uri.IsHexDigit)))
                throw new RuntimePolicyException("runtime.snapshot_invalid");
            UpdateRemoval(session, r => r with { Snapshot = snapshot, Stage = InstallationStage.Activating });
            await NotifyAsync(InstallationStage.Activating);
            // Last cancellation boundary: once removal starts the operation must finish or restore.
            ct.ThrowIfCancellationRequested();
            Save(session, session.State! with { Removal = session.State.Removal! with { RemovalStarted = true } });
            await adapter.RemoveAsync(session.State!, snapshot, CancellationToken.None);
            UpdateRemoval(session, r => r with { Stage = InstallationStage.Verifying });
            await NotifyAsync(InstallationStage.Verifying);
            if (!await adapter.VerifyRemovedAsync(CancellationToken.None))
                throw new RuntimePolicyException("runtime.removal_incomplete");
            // Everything below is irreversible; a failure is reported, never compensated with a fake restore.
            Save(session, session.State! with { Removal = session.State.Removal! with { CleanupStarted = true } });
            try
            {
                await adapter.CleanupProgramAsync(session.State!, CancellationToken.None);
                if (!plan.Request.KeepData) await adapter.DeleteDataAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RuntimePolicyException
                or System.ComponentModel.Win32Exception)
            {
                return CompleteRemoved(session, RuntimeSwitchOutcome.Failed,
                    ex is RuntimePolicyException policy ? policy.ProblemCode : "runtime.removal_cleanup_failed");
            }
            return CompleteRemoved(session, RuntimeSwitchOutcome.Succeeded, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            var cancelled = ex is OperationCanceledException && ct.IsCancellationRequested && !session.State!.Removal!.RemovalStarted;
            var problem = cancelled ? "runtime.operation_cancelled"
                : ex is RuntimePolicyException policy ? policy.ProblemCode : "runtime.removal_failed";
            return await CompensateAsync(session, problem, cancelled ? RuntimeSwitchOutcome.Cancelled : RuntimeSwitchOutcome.Failed);
        }
    }

    /// <summary>Startup reconciliation. A checkpoint before cleanup restores; a later one is closed as interrupted.</summary>
    public async Task<RuntimeSwitchResult?> RecoverAsync(Guid operationId, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new RuntimePolicyException("runtime.operation_id_invalid");
        using var session = store.Acquire();
        if (session.State?.Removal is not { } pending) return null;
        if (pending.OperationId != operationId) throw new RuntimePolicyException("runtime.recovery_target_conflict");
        ct.ThrowIfCancellationRequested();
        if (pending.CleanupStarted)
            return CompleteRemoved(session, RuntimeSwitchOutcome.Interrupted,
                pending.ProblemCode ?? "runtime.removal_cleanup_interrupted");
        return await CompensateAsync(session, pending.ProblemCode ?? "runtime.operation_interrupted", RuntimeSwitchOutcome.Interrupted);
    }

    private async Task<RuntimeSwitchResult> CompensateAsync(RuntimeArchiveStateStore.Session session, string problem,
        RuntimeSwitchOutcome outcome)
    {
        try
        {
            UpdateRemoval(session, r => r with { Stage = InstallationStage.RollingBack, ProblemCode = problem });
            await NotifyAsync(InstallationStage.RollingBack);
            var pending = session.State!.Removal!;
            if (pending.RemovalStarted && !await adapter.RestoreAsync(session.State!, pending.Snapshot!, CancellationToken.None))
                throw new RuntimePolicyException("runtime.compensation_health_failed");
            var state = session.State!;
            var receipt = new RuntimeSwitchReceipt(pending.OperationId, pending.RequestHash, pending.IdempotencyHash,
                outcome, state.Active?.RevisionId, problem, DateTimeOffset.UtcNow, pending.Snapshot);
            Save(session, state with
            {
                Installation = state.Installation with { RecoveryRequired = false },
                Removal = null, Receipts = [.. state.Receipts, receipt]
            });
            return new(receipt.OperationId, receipt.Outcome, receipt.ProblemCode);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            try
            {
                Save(session, session.State! with
                {
                    Installation = session.State!.Installation with { RecoveryRequired = true },
                    Removal = session.State.Removal! with
                    {
                        Stage = InstallationStage.Interrupted, ProblemCode = problem,
                        RecoveryProblemCode = ex is RuntimePolicyException policy ? policy.ProblemCode : "runtime.compensation_failed"
                    }
                });
            }
            catch (Exception saveError) when (saveError is IOException or UnauthorizedAccessException or RuntimePolicyException) { }
            return new(session.State!.Removal!.OperationId, RuntimeSwitchOutcome.RecoveryRequired, "runtime.recovery_required");
        }
    }

    /// <summary>Only reachable once the registration is proven gone; ownership leaves this installation.</summary>
    private static RuntimeSwitchResult CompleteRemoved(RuntimeArchiveStateStore.Session session,
        RuntimeSwitchOutcome outcome, string? problem)
    {
        var state = session.State!;
        var pending = state.Removal!;
        var keepData = pending.Plan.Request.KeepData;
        var receipt = new RuntimeSwitchReceipt(pending.OperationId, pending.RequestHash, pending.IdempotencyHash,
            outcome, null, problem, DateTimeOffset.UtcNow, pending.Snapshot);
        Save(session, state with
        {
            Installation = state.Installation with
            { Ownership = RuntimeOwnership.Absent, ActiveReleaseId = null, RecoveryRequired = false },
            Active = null,
            ConfigurationIdentity = keepData ? state.ConfigurationIdentity : null,
            Revisions = [],
            Removal = null, Receipts = [.. state.Receipts, receipt]
        });
        return new(receipt.OperationId, receipt.Outcome, receipt.ProblemCode);
    }

    private static void ValidatePlan(RuntimeChangePlan plan, RuntimeArchiveState state)
    {
        var request = plan.Request;
        var installation = state.Installation;
        if (state.Receipts.Length >= 4096) throw new RuntimePolicyException("runtime.transaction_state_limit");
        if (request.Change != RuntimeChange.Uninstall) throw new RuntimePolicyException("runtime.action_unsupported");
        if (request.ReleaseId is not null) throw new RuntimePolicyException("runtime.release_unexpected");
        if (request.InstallationId != installation.InstallationId || request.HostId != installation.HostId
            || plan.RuntimeId != installation.RuntimeId) throw new RuntimePolicyException("runtime.installation_mismatch");
        if (request.ExpectedStateRevision != installation.StateRevision) throw new RuntimePolicyException("runtime.state_revision_conflict");
        if (installation.Ownership != RuntimeOwnership.Managed || state.Active is null)
            throw new RuntimePolicyException("runtime.not_managed");
        if (plan.Release is not null || plan.PreviousReleaseId != state.Active.Release.ReleaseId)
            throw new RuntimePolicyException("runtime.state_revision_conflict");
    }

    private static void UpdateRemoval(RuntimeArchiveStateStore.Session session,
        Func<RuntimePendingRemoval, RuntimePendingRemoval> update) =>
        Save(session, session.State! with { Removal = update(session.State!.Removal!) });

    private static void Save(RuntimeArchiveStateStore.Session session, RuntimeArchiveState state) =>
        session.Save(state with { Installation = state.Installation with { StateRevision = checked(session.State!.Installation.StateRevision + 1) } });

    private async Task NotifyAsync(InstallationStage stage)
    {
        if (progress is null) return;
        try { await progress(stage); }
        catch (IOException) { /* A lost progress reader does not cancel an accepted removal. */ }
        catch (ObjectDisposedException) { }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
