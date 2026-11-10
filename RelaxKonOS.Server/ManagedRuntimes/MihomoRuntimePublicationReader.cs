using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy.Mihomo;

namespace RelaxKonOS.Server.ManagedRuntimes;

/// <summary>Combines native observations with the Server publication ledger without resolving a runtime writer.</summary>
public sealed class MihomoRuntimePublicationReader(MihomoPublicationStore store, IPrivilegedOperationTransport helper, IServerModeResolver mode)
{
    public Task<RuntimeInstallationSnapshot> ObserveAsync(CancellationToken ct) =>
        ProjectAsync(MihomoRuntimeReceiptReader.QueryAsync(helper, mode, ct, null), ct);

    /// <summary>Public observation includes unresolved Server publication work, even after root commits.
    /// It never upgrades ownership from an encrypted file or reads controller credentials.</summary>
    public Task<RuntimeInstallationSnapshot> QueryAsync(CancellationToken ct) =>
        ProjectAsync(CallAsync<RuntimeInstallationSnapshot>(new(PrivilegedOperationKind.MihomoRuntimeInventoryQuery), ct), ct);

    /// <summary>Read-only recovery facts. It observes the service but changes neither the ledger nor the service.</summary>
    public async Task<RuntimeRecoverySnapshot> RecoveryAsync(CancellationToken ct)
    {
        MihomoPublicationState state;
        try
        {
            state = await store.ReadAsync(ct);
        }
        catch (Exception ex) when (ex is RuntimePolicyException or IOException or UnauthorizedAccessException)
        {
            var unknown = await CallAsync<RuntimeRecoverySnapshot>(new(PrivilegedOperationKind.MihomoRuntimeRecoveryQuery), ct);
            return unknown with { Required = true, ProblemCode = "runtime.publication_state_invalid" };
        }
        if (state.DirectReload is { } direct)
            return new(true, direct.OperationId, null, null, false, direct.ProblemCode, null, null, null,
                CanCompensateDirectSettings: OperatingSystem.IsLinux() && direct.CanCompensate,
                CompensationOperationId: direct.Compensation?.Id);

        var observed = await CallAsync<RuntimeRecoverySnapshot>(new(PrivilegedOperationKind.MihomoRuntimeRecoveryQuery), ct);
        if (state.Pending is not { } pending) return observed;
        if (observed.OperationId is { } actual && actual != pending.OperationId)
            return observed with { Required = true, ProblemCode = "runtime.recovery_target_conflict" };
        // Server commit work can outlive the native transaction.
        return observed with
        {
            Required = true, OperationId = pending.OperationId,
            Change = pending.InstallationPlan?.Request.Change ?? pending.RemovalPlan?.Request.Change ?? observed.Change,
            ProblemCode = "runtime.publication_unconfirmed"
        };
    }

    public async Task<RuntimeResourceSnapshot> QueryResourcesAsync(CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            if ((await store.ReadAsync(ct)).Pending is not null) return new(null, [], "runtime.publication_unconfirmed");
            return await CallAsync<RuntimeResourceSnapshot>(new(PrivilegedOperationKind.MihomoRuntimeResourceQuery), ct);
        }
        catch (RuntimePolicyException ex) { throw new RuntimeLifecycleException(ex.ProblemCode); }
        finally { store.Gate.Release(); }
    }

    private async Task<RuntimeInstallationSnapshot> ProjectAsync(Task<RuntimeInstallationSnapshot> nativeObservation, CancellationToken ct)
    {
        var observed = await nativeObservation;
        try
        {
            var state = await store.ReadAsync(ct);
            if ((state.Pending?.OperationId ?? state.DirectReload?.OperationId) is not { } operation) return observed;
            var conflict = observed.PendingOperationId is { } rootOperation && rootOperation != operation;
            return observed with
            {
                Installation = observed.Installation with { RecoveryRequired = true },
                PendingOperationId = observed.PendingOperationId ?? operation,
                PendingStage = observed.PendingStage,
                ProblemCode = conflict ? "runtime.recovery_target_conflict"
                    : state.DirectReload?.ProblemCode ?? "runtime.publication_unconfirmed"
            };
        }
        catch (Exception ex) when (ex is RuntimePolicyException or IOException or UnauthorizedAccessException)
        {
            return observed with
            {
                Installation = observed.Installation with { Ownership = RuntimeOwnership.Unknown, ActiveReleaseId = null, RecoveryRequired = true },
                MeasuredVersion = null, ActiveRevisionId = null, ProblemCode = "runtime.publication_state_invalid"
            };
        }
    }

    private Task<T> CallAsync<T>(PrivilegedOperationRequest request, CancellationToken ct) =>
        MihomoRuntimeHelperProtocol.CallAsync<T>(helper, mode, request, ct);
}
