using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Proxy;

namespace RelaxKonOS.Server.Proxy.Mihomo;

public enum MihomoPublicationOutcome { Confirmed, Compensated, NotStarted, RecoveryRequired }
public sealed record MihomoPublicationResult(Guid OperationId, MihomoPublicationOutcome Outcome, RuntimeSwitchResult? Receipt);

/// <summary>
/// Owns the publication gate through staging, Helper execution and confirmation. A lost Helper response
/// never becomes success or an inferred rollback. Startup explicitly reconciles the same durable plan.
/// Application configuration/settings writers must use this coordinator when replacing direct file writes.
/// </summary>
public sealed class MihomoConfigurationPublicationService(MihomoPublicationStore store,
    MihomoRuntimeAuthorizationProvider authorization, MihomoRuntimeService runtime, IProxyPlatformPaths paths)
{
    public async Task<MihomoPublicationResult> PublishAsync(Guid operationId, long expectedPublicationRevision,
        RuntimeConfigurationPlan plan, MihomoPublishedRuntime candidate, CancellationToken ct)
    {
        if (operationId == Guid.Empty || plan is null || plan.RuntimeId != "mihomo")
            throw new RuntimePolicyException("runtime.request_invalid");
        MihomoRuntimeMaterial.Validate(candidate);
        var description = MihomoConfigurationPolicy.Validate(candidate);
        if (description.Material.NetworkTransitionRequired || candidate.NetworkTransitionRequired)
            throw new RuntimePolicyException("runtime.network_transaction_required");
        if (MihomoPublicationStore.Identity(candidate.Yaml) != plan.CandidateIdentity)
            throw new RuntimePolicyException("runtime.configuration_drift");
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            if (state.Pending is not null)
            {
                if (state.Pending.OperationId != operationId
                    || !Same(state.Pending.ConfigurationPlan, plan) || !Same(state.Pending.Candidate, candidate))
                    throw new RuntimePolicyException("runtime.publication_unconfirmed");
                // Replayed requests reconcile; they never re-send a potentially accepted configuration.
                return await RecoverUnderGateAsync(state, CancellationToken.None);
            }
            if (state.LastConfiguration is { } completed && completed.OperationId == operationId)
            {
                if (completed.RequestHash != MihomoPublicationStore.ConfigurationRequestHash(plan, candidate))
                    throw new RuntimePolicyException("runtime.idempotency_conflict");
                var observed = await runtime.QueryAsync(ct, operationId);
                RequireInstance(plan, observed);
                if (completed.NotStarted && observed.Receipt is null && observed.PendingOperationId is null
                    && !observed.Installation.RecoveryRequired && observed.ConfigurationIdentity == plan.PreviousIdentity
                    && observed.Installation.StateRevision == plan.ExpectedStateRevision)
                    return new(operationId, MihomoPublicationOutcome.NotStarted, null);
                if (observed.PendingOperationId is not null || observed.Installation.RecoveryRequired
                    || observed.ConfigurationIdentity != (completed.Confirmed ? plan.CandidateIdentity : plan.PreviousIdentity)
                    || observed.Receipt is null || completed.Confirmed != (observed.Receipt.Outcome == RuntimeSwitchOutcome.Succeeded)
                    || observed.Receipt.Outcome == RuntimeSwitchOutcome.RecoveryRequired)
                    throw new RuntimePolicyException("runtime.publication_unconfirmed");
                return new(operationId, completed.Confirmed ? MihomoPublicationOutcome.Confirmed : MihomoPublicationOutcome.Compensated, observed.Receipt);
            }
            if (state.Revision != expectedPublicationRevision || state.Identity != plan.PreviousIdentity || state.Published is null)
                throw new RuntimePolicyException("runtime.state_revision_conflict");
            await authorization.ReadAsync(ct); // Refuse drift/revoked old credentials before accepting work.
            var before = await runtime.QueryAsync(ct, operationId);
            RequireInstance(plan, before);
            if (before.PendingOperationId is not null || before.Installation.RecoveryRequired)
                throw new RuntimePolicyException("runtime.recovery_required");
            if (before.Installation.StateRevision != plan.ExpectedStateRevision || before.ConfigurationIdentity != plan.PreviousIdentity
                || before.Receipt is not null) throw new RuntimePolicyException("runtime.state_revision_conflict");
            state = await store.BeginConfigurationAsync(operationId, expectedPublicationRevision, candidate, plan, ct);
            // The accepted durable transaction survives client cancellation. Transport errors leave Pending
            // intact; the callback is disposed by the runtime service before any later reconciliation.
            var result = await runtime.ConfigureUnderGateAsync(operationId, plan, CancellationToken.None);
            return await ResolveAsync(state, await runtime.QueryAsync(CancellationToken.None, operationId), result,
                noHelperTransaction: false, CancellationToken.None);
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
            if (state.Pending?.OperationId != operationId || state.Pending.ConfigurationPlan is null)
                throw new RuntimePolicyException("runtime.publication_unconfirmed");
            return await RecoverUnderGateAsync(state, CancellationToken.None);
        }
        finally { store.Gate.Release(); }
    }

    private async Task<MihomoPublicationResult> RecoverUnderGateAsync(MihomoPublicationState state, CancellationToken ct)
    {
        var operationId = state.Pending!.OperationId;
        var observed = await runtime.QueryAsync(ct, operationId);
        RequireInstance(state.Pending.ConfigurationPlan!, observed);
        if (observed.PendingOperationId == operationId || observed.Receipt is null)
        {
            // Acquires the Helper instance lock even if no journal exists. A previous worker that had read
            // authorization would already have persisted its journal; one that has not cannot use the old,
            // now-closed Server callback to switch later. Never infer absence from a lock-free query alone.
            var result = await runtime.RecoverConfigurationUnderGateAsync(operationId, ct);
            return await ResolveAsync(state, await runtime.QueryAsync(ct, operationId), result, result is null, ct);
        }
        return await ResolveAsync(state, observed, observed.Receipt, false, ct);
    }

    private async Task<MihomoPublicationResult> ResolveAsync(MihomoPublicationState state, RuntimeInstallationSnapshot observed,
        RuntimeSwitchResult? result, bool noHelperTransaction, CancellationToken ct)
    {
        var pending = state.Pending!;
        var plan = pending.ConfigurationPlan!;
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
        if (receipt is { Outcome: RuntimeSwitchOutcome.Succeeded } && observed.ConfigurationIdentity == pending.Identity
            && observed.Installation.StateRevision > plan.ExpectedStateRevision)
        {
            await files.ReplaceAsync(state, pending.Candidate.Yaml, ct);
            await store.ConfirmAsync(pending.OperationId, observed.ConfigurationIdentity, ct);
            return new(pending.OperationId, MihomoPublicationOutcome.Confirmed, receipt);
        }
        if (receipt is { Outcome: RuntimeSwitchOutcome.Failed or RuntimeSwitchOutcome.Cancelled or RuntimeSwitchOutcome.Interrupted }
            && observed.ConfigurationIdentity == state.Identity && observed.Installation.StateRevision > plan.ExpectedStateRevision)
        {
            await files.ReplaceAsync(state, state.Published!.Yaml, ct);
            await store.AbortAsync(pending.OperationId, observed.ConfigurationIdentity, false, ct);
            return new(pending.OperationId, MihomoPublicationOutcome.Compensated, receipt);
        }
        if (noHelperTransaction && receipt is null && observed.ConfigurationIdentity == state.Identity
            && observed.Installation.StateRevision == plan.ExpectedStateRevision)
        {
            await files.ReplaceAsync(state, state.Published!.Yaml, ct);
            await store.AbortAsync(pending.OperationId, observed.ConfigurationIdentity, true, ct);
            return new(pending.OperationId, MihomoPublicationOutcome.NotStarted, null);
        }
        throw new RuntimePolicyException("runtime.publication_unconfirmed");
    }

    private static void RequireInstance(RuntimeConfigurationPlan plan, RuntimeInstallationSnapshot snapshot)
    {
        if (snapshot.Installation.InstallationId != plan.InstallationId || snapshot.Installation.HostId != plan.HostId
            || snapshot.Installation.RuntimeId != "mihomo" || snapshot.ActiveRevisionId != plan.ActiveRevisionId
            || snapshot.Installation.Ownership != RuntimeOwnership.Managed)
            throw new RuntimePolicyException("runtime.installation_mismatch");
    }
    private static bool Same<T>(T left, T right) => RuntimeJson.Serialize(left).AsSpan().SequenceEqual(RuntimeJson.Serialize(right));
}
