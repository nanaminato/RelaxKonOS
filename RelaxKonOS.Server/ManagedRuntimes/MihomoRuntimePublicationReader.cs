using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.Proxy.Mihomo;

namespace RelaxKonOS.Server.ManagedRuntimes;

/// <summary>Combines native observations with the Server publication ledger without resolving a runtime writer.</summary>
public sealed class MihomoRuntimePublicationReader(MihomoPublicationStore store, IMihomoRuntimeReceiptReader receipts)
{
    public Task<RuntimeInstallationSnapshot> ObserveAsync(CancellationToken ct) =>
        ProjectAsync(store, receipts.QueryHelperAsync(ct), ct);

    internal static async Task<RuntimeInstallationSnapshot> ProjectAsync(MihomoPublicationStore store, Task<RuntimeInstallationSnapshot> nativeObservation, CancellationToken ct)
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
}
