using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.Proxy.Mihomo;

namespace RelaxKonOS.Server.ManagedRuntimes;

/// <summary>One named recovery across native transactions and the Server publication commit.</summary>
public sealed class MihomoRuntimeRecoveryService(MihomoPublicationStore store, MihomoRuntimeService runtime,
    MihomoConfigurationPublicationService configuration, MihomoInstallationPublicationService installation,
    MihomoRemovalPublicationService removal)
{
    public async Task<RuntimeSwitchResult> RetryAsync(Guid operationId, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new RuntimeLifecycleException("runtime.request_invalid");
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            if (state.Pending is not { } pending)
                return await runtime.RecoverUnderGateAsync(Guid.NewGuid(), operationId, CancellationToken.None)
                    ?? throw new RuntimeLifecycleException("runtime.recovery_absent");
            if (pending.OperationId != operationId) throw new RuntimeLifecycleException("runtime.recovery_target_conflict");
            var result = pending.RemovalPlan is not null ? await removal.RecoverUnderGateAsync(state, CancellationToken.None)
                : pending.InstallationPlan is not null ? await installation.RecoverUnderGateAsync(state, CancellationToken.None)
                : pending.ConfigurationPlan is not null ? await configuration.RecoverUnderGateAsync(state, CancellationToken.None)
                : throw new RuntimeLifecycleException("runtime.installation_publication_required");
            // This is the combined recovery result, not a fabricated Helper receipt.
            if (result.Outcome == MihomoPublicationOutcome.Confirmed)
                return new(operationId, RuntimeSwitchOutcome.Succeeded, null);
            if (result.Outcome == MihomoPublicationOutcome.RecoveryRequired)
                return new(operationId, RuntimeSwitchOutcome.RecoveryRequired, result.Receipt?.ProblemCode ?? "runtime.publication_unconfirmed");
            return result.Receipt ?? new(operationId, RuntimeSwitchOutcome.Failed, "runtime.operation_not_started");
        }
        catch (RuntimePolicyException ex) { throw new RuntimeLifecycleException(ex.ProblemCode); }
        finally { store.Gate.Release(); }
    }
}
