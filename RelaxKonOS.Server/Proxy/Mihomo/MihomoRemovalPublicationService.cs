using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.ManagedRuntimes;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Durable Server reconciliation for runtime removal. App-owned settings and canonical credentials
/// outlive runtime uninstall; only this installation's publication and exact mirror are discarded.</summary>
public sealed class MihomoRemovalPublicationService(MihomoPublicationStore store, MihomoRuntimeService runtime,
    IProxyPlatformPaths paths, MihomoSettingsPublicationFiles settingsFiles)
{
    public async Task<MihomoPublicationResult> RemoveAsync(Guid operationId, RuntimeChangePlan plan, CancellationToken ct)
    {
        Validate(operationId, plan);
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            if (state.Pending is { } pending)
            {
                if (pending.OperationId != operationId || pending.RemovalPlan is null
                    || !RuntimeJson.Serialize(pending.RemovalPlan).AsSpan().SequenceEqual(RuntimeJson.Serialize(plan)))
                    throw new RuntimePolicyException("runtime.publication_unconfirmed");
                return await RecoverUnderGateAsync(state, CancellationToken.None);
            }
            if (state.LastRemoval is { } completed && completed.OperationId == operationId)
            {
                if (completed.RequestHash != MihomoPublicationStore.RemovalRequestHash(plan))
                    throw new RuntimePolicyException("runtime.idempotency_conflict");
                var observed = await runtime.QueryHelperAsync(ct, operationId);
                RequireInstance(plan, observed);
                if (observed.PendingOperationId is not null || observed.Installation.RecoveryRequired)
                    throw new RuntimePolicyException("runtime.recovery_required");
                if (completed.NotStarted && observed.Receipt is null && IsRestored(state, plan, observed)
                    && observed.Installation.StateRevision == plan.Request.ExpectedStateRevision)
                    return new(operationId, MihomoPublicationOutcome.NotStarted, null);
                if (completed.Confirmed && observed.Receipt is { Outcome: RuntimeSwitchOutcome.Succeeded }
                    && IsAbsent(observed) && observed.ConfigurationIdentity == state.Identity
                    && (plan.Request.KeepData ? state.Published is not null : state.Published is null))
                    return new(operationId, MihomoPublicationOutcome.Confirmed, observed.Receipt);
                if (!completed.Confirmed && IsRestored(state, plan, observed)
                    && observed.Receipt is { Outcome: RuntimeSwitchOutcome.Failed or RuntimeSwitchOutcome.Interrupted or RuntimeSwitchOutcome.Cancelled })
                    return new(operationId, MihomoPublicationOutcome.Compensated, observed.Receipt);
                throw new RuntimePolicyException("runtime.publication_unconfirmed");
            }
            var before = await runtime.QueryHelperAsync(ct, operationId);
            RequireInstance(plan, before);
            if (before.PendingOperationId is not null || before.Installation.RecoveryRequired)
                throw new RuntimePolicyException("runtime.recovery_required");
            if (!IsRestored(state, plan, before) || before.Installation.StateRevision != plan.Request.ExpectedStateRevision
                || before.Receipt is not null || await new MihomoPublicationFiles(paths).IdentityAsync(ct) != state.Identity)
                throw new RuntimePolicyException("runtime.state_revision_conflict");
            var settings = await settingsFiles.CaptureRemovalAsync(ct);
            state = await store.BeginRemovalAsync(operationId, state.Revision, plan, settings, ct);
            var receipt = await runtime.RemoveUnderGateAsync(operationId, plan, CancellationToken.None);
            return await ResolveAsync(state, await runtime.QueryHelperAsync(CancellationToken.None, operationId), receipt, false, CancellationToken.None);
        }
        finally { store.Gate.Release(); }
    }

    public async Task<MihomoPublicationResult> RecoverAsync(Guid operationId, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new RuntimePolicyException("runtime.operation_id_invalid");
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            if (state.Pending is not { RemovalPlan: not null } pending || pending.OperationId != operationId)
                throw new RuntimePolicyException("runtime.publication_unconfirmed");
            return await RecoverUnderGateAsync(state, CancellationToken.None);
        }
        finally { store.Gate.Release(); }
    }

    internal async Task<MihomoPublicationResult> RecoverUnderGateAsync(MihomoPublicationState state, CancellationToken ct)
    {
        var pending = state.Pending!;
        var observed = await runtime.QueryHelperAsync(ct, pending.OperationId);
        RequireInstance(pending.RemovalPlan!, observed);
        if (observed.PendingOperationId == pending.OperationId || observed.Receipt is null)
        {
            var receipt = await runtime.RecoverRemovalUnderGateAsync(pending.OperationId, ct);
            return await ResolveAsync(state, await runtime.QueryHelperAsync(ct, pending.OperationId), receipt, receipt is null, ct);
        }
        return await ResolveAsync(state, observed, observed.Receipt, false, ct);
    }

    private async Task<MihomoPublicationResult> ResolveAsync(MihomoPublicationState state, RuntimeInstallationSnapshot observed,
        RuntimeSwitchResult? result, bool noHelperTransaction, CancellationToken ct)
    {
        var pending = state.Pending!;
        var plan = pending.RemovalPlan!;
        RequireInstance(plan, observed);
        if (observed.PendingOperationId == pending.OperationId)
        {
            if (result is not { Outcome: RuntimeSwitchOutcome.RecoveryRequired } || !observed.Installation.RecoveryRequired)
                throw new RuntimePolicyException("runtime.publication_unconfirmed");
            return new(pending.OperationId, MihomoPublicationOutcome.RecoveryRequired, result);
        }
        if (observed.PendingOperationId is not null || observed.Installation.RecoveryRequired)
            throw new RuntimePolicyException("runtime.recovery_required");
        var receipt = observed.Receipt;
        if (receipt is not null && (receipt.OperationId != pending.OperationId || result is not null && receipt != result))
            throw new RuntimePolicyException("runtime.operation_id_conflict");
        var files = new MihomoPublicationFiles(paths);
        if (receipt is { Outcome: RuntimeSwitchOutcome.Succeeded } && IsAbsent(observed)
            && observed.Installation.StateRevision > plan.Request.ExpectedStateRevision
            && observed.ConfigurationIdentity == (plan.Request.KeepData ? state.Identity : null))
        {
            if (plan.Request.KeepData)
            {
                if (await files.IdentityAsync(ct) != state.Identity) throw new RuntimePolicyException("runtime.publication_unconfirmed");
            }
            else if (files.Exists() && await files.IdentityAsync(ct) != state.Identity)
                throw new RuntimePolicyException("runtime.publication_unconfirmed");
            if (pending.Settings is not { } settings) throw new RuntimePolicyException("runtime.publication_state_invalid");
            // Root removal is already proved. Disable only this application's owned host proxy, then
            // persist its disabled preference before confirming the Server side of the same operation.
            await settingsFiles.CommitRemovalAsync(settings, ct);
            var after = await runtime.QueryHelperAsync(ct, pending.OperationId);
            RequireInstance(plan, after);
            if (!IsAbsent(after) || after.PendingOperationId is not null || after.Installation.RecoveryRequired
                || after.Installation.StateRevision != observed.Installation.StateRevision
                || after.ConfigurationIdentity != observed.ConfigurationIdentity || after.Receipt != receipt)
                throw new RuntimePolicyException("runtime.publication_unconfirmed");
            if (!plan.Request.KeepData) await files.DeleteRemovedAsync(state, ct);
            await store.CompleteRemovalAsync(pending.OperationId, observed.ConfigurationIdentity, ct);
            return new(pending.OperationId, MihomoPublicationOutcome.Confirmed, receipt);
        }
        var notStarted = noHelperTransaction && receipt is null
            && observed.Installation.StateRevision == plan.Request.ExpectedStateRevision;
        if ((notStarted || receipt is { Outcome: RuntimeSwitchOutcome.Failed or RuntimeSwitchOutcome.Cancelled or RuntimeSwitchOutcome.Interrupted })
            && IsRestored(state, plan, observed) && await files.IdentityAsync(ct) == state.Identity)
        {
            await store.AbortAsync(pending.OperationId, observed.ConfigurationIdentity, notStarted, ct);
            return new(pending.OperationId, notStarted ? MihomoPublicationOutcome.NotStarted : MihomoPublicationOutcome.Compensated, receipt);
        }
        throw new RuntimePolicyException("runtime.publication_unconfirmed");
    }

    private static bool IsAbsent(RuntimeInstallationSnapshot observed) => observed.Installation.Ownership == RuntimeOwnership.Absent
        && observed.ActiveRevisionId is null && observed.Installation.ActiveReleaseId is null && observed.MeasuredVersion is null;
    private static bool IsRestored(MihomoPublicationState state, RuntimeChangePlan plan, RuntimeInstallationSnapshot observed) =>
        state.Published is not null && observed.Installation.Ownership == RuntimeOwnership.Managed && observed.ActiveRevisionId is not null
        && observed.Installation.ActiveReleaseId == plan.PreviousReleaseId && observed.ConfigurationIdentity == state.Identity;
    private static void RequireInstance(RuntimeChangePlan plan, RuntimeInstallationSnapshot observed)
    {
        if (observed.Installation.InstallationId != plan.Request.InstallationId || observed.Installation.HostId != plan.Request.HostId
            || observed.Installation.RuntimeId != "mihomo") throw new RuntimePolicyException("runtime.installation_mismatch");
    }
    private static void Validate(Guid operationId, RuntimeChangePlan plan)
    {
        if (operationId == Guid.Empty) throw new RuntimePolicyException("runtime.operation_id_invalid");
        if (plan is not { RuntimeId: "mihomo", Release: null, Request: { Change: RuntimeChange.Uninstall, ReleaseId: null } }
            || plan.Request.InstallationId == Guid.Empty || string.IsNullOrWhiteSpace(plan.Request.HostId)
            || string.IsNullOrWhiteSpace(plan.PreviousReleaseId) || plan.Request.ExpectedStateRevision < 0
            || string.IsNullOrWhiteSpace(plan.Request.IdempotencyKey) || plan.Request.IdempotencyKey.Length > 128
            || plan.Request.IdempotencyKey.Any(c => c < 33 || c > 126)) throw new RuntimePolicyException("runtime.request_invalid");
    }
}
