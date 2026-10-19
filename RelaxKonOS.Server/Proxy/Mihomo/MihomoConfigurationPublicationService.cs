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
    MihomoRuntimeAuthorizationProvider authorization, MihomoRuntimeService runtime, IProxyPlatformPaths paths,
    MihomoSettingsPublicationFiles? settingsFiles = null, IProxyProfileRepository? profiles = null)
{
    public async Task<MihomoPublicationResult> PublishAsync(Guid operationId, long expectedPublicationRevision,
        RuntimeConfigurationPlan plan, MihomoPublishedRuntime candidate, CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try { return await PublishUnderGateAsync(operationId, expectedPublicationRevision, plan, candidate, ct); }
        finally { store.Gate.Release(); }
    }

    // Application profile writers already hold the publication gate while selecting current material.
    internal async Task<MihomoPublicationResult> PublishUnderGateAsync(Guid operationId, long expectedPublicationRevision,
        RuntimeConfigurationPlan plan, MihomoPublishedRuntime candidate, CancellationToken ct, MihomoSettingsPublication? settings = null, MihomoProfilePublication? profile = null)
    {
        if (operationId == Guid.Empty || plan is null || plan.RuntimeId != "mihomo")
            throw new RuntimePolicyException("runtime.request_invalid");
        MihomoRuntimeMaterial.Validate(candidate);
        var description = MihomoConfigurationPolicy.Validate(candidate);
        if (description.Material.NetworkTransitionRequired || candidate.NetworkTransitionRequired)
            throw new RuntimePolicyException("runtime.network_transaction_required");
        if (MihomoPublicationStore.Identity(candidate.Yaml) != plan.CandidateIdentity)
            throw new RuntimePolicyException("runtime.configuration_drift");
        var state = await store.ReadAsync(ct);
        if (state.Pending is not null)
        {
            if (state.Pending.OperationId != operationId
                || !Same(state.Pending.ConfigurationPlan, plan) || !Same(state.Pending.Candidate, candidate) || !Same(state.Pending.Settings, settings) || !Same(state.Pending.Profile, profile))
                throw new RuntimePolicyException("runtime.publication_unconfirmed");
            // Replayed requests reconcile; they never re-send a potentially accepted configuration.
            return await RecoverUnderGateAsync(state, CancellationToken.None);
        }
        if (state.LastConfiguration is { } completed && completed.OperationId == operationId)
        {
            if (completed.RequestHash != MihomoPublicationStore.ConfigurationRequestHash(plan, candidate, settings, profile))
                throw new RuntimePolicyException("runtime.idempotency_conflict");
            var observed = await runtime.QueryHelperAsync(ct, operationId);
            RequireInstance(plan, observed);
            if ((settings is not null || profile is not null) && plan.PreviousIdentity == plan.CandidateIdentity && completed.Confirmed
                && observed.PendingOperationId is null && !observed.Installation.RecoveryRequired
                && observed.Installation.StateRevision == plan.ExpectedStateRevision && observed.ConfigurationIdentity == plan.CandidateIdentity)
                return new(operationId, MihomoPublicationOutcome.Confirmed, null);
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
        var before = await runtime.QueryHelperAsync(ct, operationId);
        RequireInstance(plan, before);
        if (before.PendingOperationId is not null || before.Installation.RecoveryRequired)
            throw new RuntimePolicyException("runtime.recovery_required");
        if (before.Installation.StateRevision != plan.ExpectedStateRevision || before.ConfigurationIdentity != plan.PreviousIdentity
            || before.Receipt is not null) throw new RuntimePolicyException("runtime.state_revision_conflict");
        if (settings is not null)
        {
            if (settingsFiles is null || !Same(MihomoSettingsPublicationFiles.Candidate(state.Published, settings.Candidate), candidate))
                throw new RuntimePolicyException("runtime.settings_drift");
            await settingsFiles.VerifyAsync(settings, allowCandidate: false, ct);
        }
        if (profile is not null)
        {
            if (profiles is null) throw new RuntimePolicyException("runtime.profile_publication_required");
            await profiles.ValidatePublicationAsync(profile, ct);
        }
        state = await store.BeginConfigurationAsync(operationId, expectedPublicationRevision, candidate, plan, ct, settings, profile);
        if (plan.PreviousIdentity == plan.CandidateIdentity)
            return await ResolveApplicationOnlyAsync(state, CancellationToken.None);
        // The accepted durable transaction survives client cancellation. Transport errors leave Pending
        // intact; the callback is disposed by the runtime service before any later reconciliation.
        var result = await runtime.ConfigureUnderGateAsync(operationId, plan, CancellationToken.None);
        return await ResolveAsync(state, await runtime.QueryHelperAsync(CancellationToken.None, operationId), result,
            noHelperTransaction: false, CancellationToken.None);
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

    internal async Task<MihomoPublicationResult> RecoverUnderGateAsync(MihomoPublicationState state, CancellationToken ct)
    {
        var operationId = state.Pending!.OperationId;
        if ((state.Pending.Settings is not null || state.Pending.Profile is not null) && state.Pending.ConfigurationPlan!.PreviousIdentity == state.Pending.Identity)
            return await ResolveApplicationOnlyAsync(state, ct);
        var observed = await runtime.QueryHelperAsync(ct, operationId);
        RequireInstance(state.Pending.ConfigurationPlan!, observed);
        if (observed.PendingOperationId == operationId || observed.Receipt is null)
        {
            // Acquires the Helper instance lock even if no journal exists. A previous worker that had read
            // authorization would already have persisted its journal; one that has not cannot use the old,
            // now-closed Server callback to switch later. Never infer absence from a lock-free query alone.
            var result = await runtime.RecoverConfigurationUnderGateAsync(operationId, ct);
            return await ResolveAsync(state, await runtime.QueryHelperAsync(ct, operationId), result, result is null, ct);
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
            if (pending.Settings is not null || pending.Profile is not null)
            {
                await CommitApplicationAsync(pending, ct);
                var after = await runtime.QueryHelperAsync(ct, pending.OperationId);
                RequireInstance(plan, after);
                if (after.PendingOperationId is not null || after.Installation.RecoveryRequired
                    || after.Installation.StateRevision != observed.Installation.StateRevision
                    || after.ConfigurationIdentity != pending.Identity || after.Receipt != receipt)
                    throw new RuntimePolicyException("runtime.publication_unconfirmed");
            }
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

    private async Task CommitApplicationAsync(MihomoPendingPublication pending, CancellationToken ct)
    {
        if ((await authorization.ReadConfigurationAsync(pending.OperationId, ct)).Candidate is null)
            throw new RuntimePolicyException("runtime.credentials_revoked");
        if (pending.Settings is { } settings)
        {
            if (settingsFiles is null) throw new RuntimePolicyException("runtime.settings_publication_required");
            await settingsFiles.CommitAsync(settings, ct);
        }
        if (pending.Profile is { } profile)
        {
            if (profiles is null) throw new RuntimePolicyException("runtime.profile_publication_required");
            await profiles.CommitPublicationAsync(pending.OperationId, profile, pending.Identity, ct);
        }
    }

    private async Task<MihomoPublicationResult> ResolveApplicationOnlyAsync(MihomoPublicationState state, CancellationToken ct)
    {
        var pending = state.Pending!;
        var plan = pending.ConfigurationPlan!;
        if ((pending.Settings is null && pending.Profile is null) || plan.PreviousIdentity != pending.Identity)
            throw new RuntimePolicyException("runtime.publication_unconfirmed");
        async Task VerifyRuntimeAsync()
        {
            var observed = await runtime.QueryHelperAsync(ct, pending.OperationId);
            RequireInstance(plan, observed);
            if (observed.PendingOperationId is not null || observed.Installation.RecoveryRequired
                || observed.Installation.StateRevision != plan.ExpectedStateRevision || observed.ConfigurationIdentity != pending.Identity)
                throw new RuntimePolicyException("runtime.publication_unconfirmed");
        }
        await VerifyRuntimeAsync();
        await CommitApplicationAsync(pending, ct);
        await VerifyRuntimeAsync();
        await store.ConfirmAsync(pending.OperationId, pending.Identity, ct);
        // No Helper transaction was needed, and no Helper success receipt is fabricated.
        return new(pending.OperationId, MihomoPublicationOutcome.Confirmed, null);
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
