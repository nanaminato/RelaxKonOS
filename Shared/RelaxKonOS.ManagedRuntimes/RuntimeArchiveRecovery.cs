using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes;

/// <summary>
/// Operator entry for the one unfinished transaction. It never prepares a revision, re-runs capture, or
/// re-creates credentials: retrying compensation stays the transaction's own job. Closing only records what
/// the durable checkpoint still proves, and it never claims a running service or a verified configuration.
/// </summary>
public static class RuntimeArchiveRecovery
{
    public const string ClosedProblemCode = "runtime.recovery_closed_by_operator";

    /// <summary>Ledger-only description. Live service state is added by the component that owns the fixed host.</summary>
    public static RuntimeRecoverySnapshot Describe(RuntimeArchiveState? state)
    {
        if (state?.Pending is { } pending)
            return new(true, pending.OperationId, pending.Plan.Request.Change, pending.Stage, pending.SwitchStarted,
                pending.ProblemCode, pending.RecoveryProblemCode,
                pending.Plan.Release?.ReleaseId, pending.Previous?.Release.ReleaseId);
        if (state?.Removal is { } removal)
            return new(true, removal.OperationId, RuntimeChange.Uninstall, removal.Stage, removal.RemovalStarted,
                removal.ProblemCode, removal.RecoveryProblemCode, null, state.Active?.Release.ReleaseId);
        if (state?.Configuration is { } configuration)
            return new(true, configuration.OperationId, RuntimeChange.Configure, configuration.Stage, configuration.SwitchStarted,
                configuration.ProblemCode, configuration.RecoveryProblemCode, state.Active?.Release.ReleaseId, state.Active?.Release.ReleaseId);
        return new(false, null, null, null, false, null, null, null, null);
    }

    /// <summary>
    /// A stop or close may only address the transaction the Host currently holds. An unknown or already
    /// closed operation is rejected instead of being treated as a no-op success.
    /// </summary>
    public static RuntimeRecoverySnapshot Require(RuntimeRecoverySnapshot snapshot, Guid operationId)
    {
        if (operationId == Guid.Empty) throw new RuntimePolicyException("runtime.operation_id_invalid");
        if (!snapshot.Required) throw new RuntimePolicyException("runtime.recovery_absent");
        if (snapshot.OperationId != operationId) throw new RuntimePolicyException("runtime.recovery_target_conflict");
        return snapshot;
    }

    /// <summary>
    /// Closes a transaction whose compensation already failed. The caller must have stopped the service and
    /// must have confirmed the consequence; both are re-checked here rather than assumed from an earlier step.
    /// </summary>
    public static RuntimeSwitchResult Close(RuntimeArchiveStateStore.Session session, Guid operationId,
        bool confirmed, RuntimeServiceObservation? observation)
    {
        var state = session.State ?? throw new RuntimePolicyException("runtime.installation_observation_required");
        var target = state.Pending?.OperationId ?? state.Removal?.OperationId ?? state.Configuration?.OperationId
            ?? throw new RuntimePolicyException("runtime.recovery_absent");
        if (target != operationId) throw new RuntimePolicyException("runtime.recovery_target_conflict");
        // Closing a merely unfinished transaction would skip compensation that is still expected to work.
        if (!state.Installation.RecoveryRequired) throw new RuntimePolicyException("runtime.recovery_not_required");
        if (!confirmed) throw new RuntimePolicyException("runtime.recovery_confirmation_required");
        // An unobserved or still-running program would be hidden by closing; the stop must be real.
        if (observation is not { Running: false }) throw new RuntimePolicyException("runtime.recovery_stop_required");
        if (state.Configuration is { } configuration)
        {
            var receipt = new RuntimeSwitchReceipt(operationId, configuration.RequestHash, configuration.IdempotencyHash,
                RuntimeSwitchOutcome.Failed, state.Active!.RevisionId, ClosedProblemCode, DateTimeOffset.UtcNow, configuration.Snapshot);
            Save(session, state with { Configuration = null, Installation = state.Installation with { RecoveryRequired = false },
                Receipts = [.. state.Receipts, receipt] });
            return new(operationId, receipt.Outcome, receipt.ProblemCode);
        }
        return state.Pending is { } pending ? CloseSwitch(session, state, pending)
            : CloseRemoval(session, state, state.Removal!);
    }

    /// <summary>
    /// The committed pointer survives; the record never upgrades itself to "verified". A switched pointer is
    /// kept because its immutable program files exist, and every later reader (configuration publication,
    /// removal preflight, health check) re-derives the truth from disk instead of trusting this receipt.
    /// </summary>
    private static RuntimeSwitchResult CloseSwitch(RuntimeArchiveStateStore.Session session, RuntimeArchiveState state,
        RuntimePendingSwitch pending)
    {
        var active = state.Active;
        var receipt = new RuntimeSwitchReceipt(pending.OperationId, pending.RequestHash, pending.IdempotencyHash,
            RuntimeSwitchOutcome.Failed, active?.RevisionId, ClosedProblemCode, DateTimeOffset.UtcNow, pending.Snapshot);
        Save(session, state with
        {
            Installation = state.Installation with
            {
                RecoveryRequired = false,
                ActiveReleaseId = active?.Release.ReleaseId,
                Ownership = active is null ? RuntimeOwnership.Absent : RuntimeOwnership.Managed
            },
            // The rejected candidate never becomes a rollback target unless it is already the committed pointer.
            Revisions = state.Revisions
                .Where(r => r.RevisionId != pending.OperationId || r.RevisionId == active?.RevisionId).ToArray(),
            Pending = null,
            Receipts = [.. state.Receipts, receipt]
        });
        return new(receipt.OperationId, receipt.Outcome, receipt.ProblemCode);
    }

    private static RuntimeSwitchResult CloseRemoval(RuntimeArchiveStateStore.Session session, RuntimeArchiveState state,
        RuntimePendingRemoval pending)
    {
        var receipt = new RuntimeSwitchReceipt(pending.OperationId, pending.RequestHash, pending.IdempotencyHash,
            RuntimeSwitchOutcome.Failed, null, ClosedProblemCode, DateTimeOffset.UtcNow, pending.Snapshot);
        if (!pending.RemovalStarted)
        {
            // No registration was removed and no file was deleted: the captured state is still the real one.
            Save(session, state with
            {
                Installation = state.Installation with { RecoveryRequired = false },
                Removal = null, Receipts = [.. state.Receipts, receipt]
            });
            return new(receipt.OperationId, receipt.Outcome, receipt.ProblemCode);
        }
        // The registration is gone and files may be partly deleted, so ownership leaves this installation for
        // the same reason a completed removal drops it. Deletion is not retried here; the receipt says so.
        Save(session, state with
        {
            Installation = state.Installation with
            { Ownership = RuntimeOwnership.Absent, ActiveReleaseId = null, RecoveryRequired = false },
            Active = null,
            ConfigurationIdentity = pending.Plan.Request.KeepData ? state.ConfigurationIdentity : null,
            Revisions = [],
            Removal = null, Receipts = [.. state.Receipts, receipt]
        });
        return new(receipt.OperationId, receipt.Outcome, receipt.ProblemCode);
    }

    private static void Save(RuntimeArchiveStateStore.Session session, RuntimeArchiveState state) =>
        session.Save(state with
        { Installation = state.Installation with { StateRevision = checked(session.State!.Installation.StateRevision + 1) } });
}
