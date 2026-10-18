using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Proxy;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Accepts an explicit frozen Install, never "latest". Candidate authority remains pending until
/// the exact Helper receipt and installed revision/configuration are measured. It never starts the service.</summary>
public sealed class MihomoInstallationPublicationService(MihomoPublicationStore store,
    MihomoRuntimeAuthorizationProvider authorization, MihomoRuntimeService runtime, IProxyPlatformPaths paths)
{
    public async Task<MihomoPublicationResult> InstallAsync(Guid operationId, long expectedPublicationRevision,
        RuntimeChangePlan plan, MihomoPublishedRuntime candidate, CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try { return await InstallUnderGateAsync(operationId, expectedPublicationRevision, plan, candidate, ct); }
        finally { store.Gate.Release(); }
    }

    /// <summary>The default bootstrap selects settings/material under this same gate before acceptance.</summary>
    internal async Task<MihomoPublicationResult> InstallUnderGateAsync(Guid operationId, long expectedPublicationRevision,
        RuntimeChangePlan plan, MihomoPublishedRuntime candidate, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new RuntimePolicyException("runtime.operation_id_invalid");
        _ = MihomoRuntimeMaterial.InstallationPlanIdentity(plan);
        MihomoRuntimeMaterial.Validate(candidate);
        var description = MihomoConfigurationPolicy.Validate(candidate);
        if (candidate.NetworkTransitionRequired || description.Material.NetworkTransitionRequired)
            throw new RuntimePolicyException("runtime.network_transaction_required");
        var state = await store.ReadAsync(ct);
        if (state.Pending is { } pending)
        {
            if (pending.OperationId != operationId || !Same(pending.InstallationPlan, plan) || !Same(pending.Candidate, candidate))
                throw new RuntimePolicyException("runtime.publication_unconfirmed");
            return await RecoverUnderGateAsync(state, CancellationToken.None);
        }
        if (state.LastInstallation is { } completed && completed.OperationId == operationId)
        {
            if (completed.RequestHash != MihomoPublicationStore.InstallationRequestHash(plan, candidate))
                throw new RuntimePolicyException("runtime.idempotency_conflict");
            var observed = await runtime.QueryAsync(ct, operationId);
            RequireInstance(plan, observed);
            if (observed.PendingOperationId is not null || observed.Installation.RecoveryRequired)
                throw new RuntimePolicyException("runtime.recovery_required");
            if (completed.NotStarted && observed.Receipt is null && IsAbsent(observed)
                && observed.Installation.StateRevision == plan.Request.ExpectedStateRevision && observed.ConfigurationIdentity == state.Identity)
                return new(operationId, MihomoPublicationOutcome.NotStarted, null);
            if (completed.Confirmed && IsInstalled(plan, operationId, observed) && observed.Receipt is { Outcome: RuntimeSwitchOutcome.Succeeded }
                && observed.ConfigurationIdentity == MihomoPublicationStore.Identity(candidate.Yaml))
                return new(operationId, MihomoPublicationOutcome.Confirmed, observed.Receipt);
            if (!completed.Confirmed && IsAbsent(observed) && observed.ConfigurationIdentity == state.Identity
                && observed.Receipt is { Outcome: RuntimeSwitchOutcome.Failed or RuntimeSwitchOutcome.Cancelled or RuntimeSwitchOutcome.Interrupted })
                return new(operationId, MihomoPublicationOutcome.Compensated, observed.Receipt);
            throw new RuntimePolicyException("runtime.publication_unconfirmed");
        }
        if (state.Revision != expectedPublicationRevision) throw new RuntimePolicyException("runtime.state_revision_conflict");
        var before = await runtime.QueryAsync(ct, operationId);
        RequireInstance(plan, before);
        if (before.PendingOperationId is not null || before.Installation.RecoveryRequired)
            throw new RuntimePolicyException("runtime.recovery_required");
        if (!IsAbsent(before) || before.Installation.StateRevision != plan.Request.ExpectedStateRevision
            || before.ConfigurationIdentity != state.Identity || before.Receipt is not null)
            throw new RuntimePolicyException("runtime.state_revision_conflict");
        if (state.Published is not null)
        {
            if (!Same(state.Published, candidate)) throw new RuntimePolicyException("runtime.authorization_changed");
            await authorization.ReadAsync(ct);
        }
        else if (new MihomoPublicationFiles(paths).Exists())
            throw new RuntimePolicyException("runtime.publication_unconfirmed");
        state = await store.BeginInstallationAsync(operationId, expectedPublicationRevision, candidate, plan, ct);
        // No client cancellation after durable acceptance. Lost responses retain the same frozen plan.
        var result = await runtime.InstallUnderGateAsync(operationId, plan, CancellationToken.None);
        return await ResolveAsync(state, await runtime.QueryAsync(CancellationToken.None, operationId), result, false, CancellationToken.None);
    }

    public async Task<MihomoPublicationResult> RecoverAsync(Guid operationId, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new RuntimePolicyException("runtime.operation_id_invalid");
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            if (state.Pending?.OperationId != operationId || state.Pending.InstallationPlan is null)
                throw new RuntimePolicyException("runtime.publication_unconfirmed");
            return await RecoverUnderGateAsync(state, CancellationToken.None);
        }
        finally { store.Gate.Release(); }
    }

    private async Task<MihomoPublicationResult> RecoverUnderGateAsync(MihomoPublicationState state, CancellationToken ct)
    {
        var pending = state.Pending!;
        var observed = await runtime.QueryAsync(ct, pending.OperationId);
        RequireInstance(pending.InstallationPlan!, observed);
        if (observed.PendingOperationId == pending.OperationId || observed.Receipt is null)
        {
            var result = await runtime.RecoverInstallationUnderGateAsync(pending.OperationId, ct);
            return await ResolveAsync(state, await runtime.QueryAsync(ct, pending.OperationId), result, result is null, ct);
        }
        return await ResolveAsync(state, observed, observed.Receipt, false, ct);
    }

    private async Task<MihomoPublicationResult> ResolveAsync(MihomoPublicationState state, RuntimeInstallationSnapshot observed,
        RuntimeSwitchResult? result, bool noHelperTransaction, CancellationToken ct)
    {
        var pending = state.Pending!;
        var plan = pending.InstallationPlan!;
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
        if (receipt is not null && (receipt.OperationId != pending.OperationId || result is not null && result != receipt))
            throw new RuntimePolicyException("runtime.operation_id_conflict");
        var files = new MihomoPublicationFiles(paths);
        if (receipt is { Outcome: RuntimeSwitchOutcome.Succeeded } && IsInstalled(plan, pending.OperationId, observed)
            && observed.ConfigurationIdentity == pending.Identity && observed.Installation.StateRevision > plan.Request.ExpectedStateRevision)
        {
            if (state.Published is null) await files.InitializeAsync(state, ct);
            else await files.ReplaceAsync(state, pending.Candidate.Yaml, ct);
            await store.ConfirmAsync(pending.OperationId, pending.Identity, ct);
            return new(pending.OperationId, MihomoPublicationOutcome.Confirmed, receipt);
        }
        if (IsAbsent(observed) && observed.ConfigurationIdentity == state.Identity)
        {
            var notStarted = noHelperTransaction && receipt is null && observed.Installation.StateRevision == plan.Request.ExpectedStateRevision;
            var compensated = receipt is { Outcome: RuntimeSwitchOutcome.Failed or RuntimeSwitchOutcome.Cancelled or RuntimeSwitchOutcome.Interrupted }
                && observed.Installation.StateRevision > plan.Request.ExpectedStateRevision;
            if (notStarted || compensated)
            {
                if (state.Published is null)
                {
                    if (files.Exists()) throw new RuntimePolicyException("runtime.publication_unconfirmed");
                }
                else await files.ReplaceAsync(state, state.Published.Yaml, ct);
                await store.AbortAsync(pending.OperationId, observed.ConfigurationIdentity, notStarted, ct);
                return new(pending.OperationId, notStarted ? MihomoPublicationOutcome.NotStarted : MihomoPublicationOutcome.Compensated, receipt);
            }
        }
        throw new RuntimePolicyException("runtime.publication_unconfirmed");
    }

    private static void RequireInstance(RuntimeChangePlan plan, RuntimeInstallationSnapshot observed)
    {
        if (observed.Installation.InstallationId != plan.Request.InstallationId || observed.Installation.HostId != plan.Request.HostId
            || observed.Installation.RuntimeId != "mihomo" || observed.Installation.Target != plan.Release!.Target)
            throw new RuntimePolicyException("runtime.installation_mismatch");
    }
    private static bool IsAbsent(RuntimeInstallationSnapshot observed) => observed.Installation.Ownership == RuntimeOwnership.Absent
        && observed.ActiveRevisionId is null && observed.Installation.ActiveReleaseId is null;
    private static bool IsInstalled(RuntimeChangePlan plan, Guid operationId, RuntimeInstallationSnapshot observed) =>
        observed.Installation.Ownership == RuntimeOwnership.Managed && observed.ActiveRevisionId == operationId
        && observed.Installation.ActiveReleaseId == plan.Release!.ReleaseId
        && MihomoNativeValidation.TryTag(plan.Release.OriginalVersion, out var target)
        && RuntimeSemanticVersion.TryParse(observed.MeasuredVersion, out var measured) && target.Normalized == measured.Normalized;
    private static bool Same<T>(T left, T right) => RuntimeJson.Serialize(left).AsSpan().SequenceEqual(RuntimeJson.Serialize(right));
}
