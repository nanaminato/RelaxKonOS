using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy.Mihomo;

namespace RelaxKonOS.Server.ManagedRuntimes;

/// <summary>Server composition: published-state gate, per-operation authenticated callback, and installed Helper.</summary>
public sealed class MihomoRuntimeService(IPrivilegedOperationTransport helper, MihomoPublicationStore store,
    MihomoRuntimeAuthorizationProvider authorization, IServerModeResolver mode, IRuntimeDownloadProxyProvider downloadProxy)
{
    /// <summary>Public observation includes unresolved Server publication work, even after root commits.
    /// It never upgrades ownership from an encrypted file or reads controller credentials.</summary>
    public Task<RuntimeInstallationSnapshot> QueryAsync(CancellationToken ct) =>
        MihomoRuntimePublicationReader.ProjectAsync(store, CallAsync<RuntimeInstallationSnapshot>(new(PrivilegedOperationKind.MihomoRuntimeInventoryQuery), ct), ct);

    /// <summary>Coordinators already own the Server authority; only actual Helper facts can confirm it.</summary>
    internal Task<RuntimeInstallationSnapshot> QueryHelperAsync(CancellationToken ct, Guid? receiptOperationId = null) =>
        MihomoRuntimeReceiptReader.QueryAsync(helper, mode, ct, receiptOperationId);

    public async Task<RuntimePlanResult> PlanAsync(string? sourceId, RuntimePlanRequest request, CancellationToken ct)
    {
        // Removal must stay admissible when the source has expired, been revoked, or is gone.
        var id = request.Change == RuntimeChange.Uninstall && string.IsNullOrWhiteSpace(sourceId) ? "removal" : sourceId;
        if (!RuntimeValidation.Identifier(id)) throw new RuntimeLifecycleException("runtime.source_invalid");
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            if (state.Pending is not null || state.DirectReload is not null) return new(null, ["runtime.publication_unconfirmed"]);
            return await CallAsync<RuntimePlanResult>(new(PrivilegedOperationKind.MihomoRuntimePlan,
                RuntimeLifecycle: new(SourceId: id, Plan: request)), ct);
        }
        catch (RuntimePolicyException ex) { throw new RuntimeLifecycleException(ex.ProblemCode); }
        finally { store.Gate.Release(); }
    }

    public async Task<RuntimeSwitchResult> ExecuteAsync(Guid operationId, RuntimeChangePlan plan, CancellationToken ct)
    {
        if (operationId == Guid.Empty || plan is null || plan.Request is null || plan.RuntimeId != "mihomo")
            throw new RuntimeLifecycleException("runtime.request_invalid");
        if (plan.Request.Change == RuntimeChange.Install)
            throw new RuntimeLifecycleException("runtime.installation_publication_required");
        if (plan.Request.Change == RuntimeChange.Uninstall)
            throw new RuntimeLifecycleException("runtime.removal_publication_required");
        // Install/removal have separate publication coordinators; program changes name an exact artifact.
        if (plan.Release is null)
            throw new RuntimeLifecycleException("runtime.request_invalid");
        await store.Gate.WaitAsync(ct);
        try
        {
            if ((await store.ReadAsync(ct)).DirectReload is not null) throw new RuntimeLifecycleException("runtime.publication_unconfirmed");
            await using var callback = new RuntimeAuthorizationServer<MihomoCurrentAuthorization>(operationId, authorization.ReadAsync);
            var result = await CallAsync<RuntimeSwitchResult>(new(PrivilegedOperationKind.MihomoRuntimeExecute,
                OperationId: operationId, RuntimeLifecycle: new(Authorization: callback.Channel, FrozenPlan: plan),
                DownloadProxy: await downloadProxy.ResolveAsync(ct)), CancellationToken.None);
            if (result.OperationId != operationId) throw new RuntimeLifecycleException("runtime.operation_id_conflict");
            return result;
        }
        finally { store.Gate.Release(); }
    }

    public async Task<RuntimeSwitchResult?> RecoverAsync(Guid requestOperationId, Guid recoveryOperationId, CancellationToken ct)
    {
        if (requestOperationId == Guid.Empty || recoveryOperationId == Guid.Empty) throw new RuntimeLifecycleException("runtime.request_invalid");
        await store.Gate.WaitAsync(ct);
        try
        {
            return await RecoverUnderGateAsync(requestOperationId, recoveryOperationId, CancellationToken.None);
        }
        finally { store.Gate.Release(); }
    }

    internal Task<RuntimeSwitchResult?> RecoverUnderGateAsync(Guid requestOperationId, Guid recoveryOperationId,
        CancellationToken ct) =>
        MihomoRuntimeHelperProtocol.RecoverUnderGateAsync(helper, mode, authorization, requestOperationId, recoveryOperationId, ct);

    /// <summary>The configuration publication coordinator already owns Gate, including confirmation.</summary>
    internal async Task<RuntimeSwitchResult> ConfigureUnderGateAsync(Guid operationId, RuntimeConfigurationPlan plan,
        CancellationToken ct)
    {
        await using var callback = new RuntimeAuthorizationServer<MihomoConfigurationAuthorization>(operationId,
            token => authorization.ReadConfigurationAsync(operationId, token));
        var result = await CallAsync<RuntimeSwitchResult>(new(PrivilegedOperationKind.MihomoRuntimeConfigure,
            OperationId: operationId, RuntimeLifecycle: new(Authorization: callback.Channel, ConfigurationPlan: plan)), ct);
        if (result.OperationId != operationId) throw new RuntimeLifecycleException("runtime.operation_id_conflict");
        return result;
    }

    // The caller holds the publication gate for the lifetime of the callback and the privileged action.
    internal async Task<RuntimeSwitchResult> StartManagedTunUnderGateAsync(Guid executionId, Guid publicationId,
        bool restart, CancellationToken ct)
    {
        if (executionId == Guid.Empty || publicationId == Guid.Empty || executionId == publicationId)
            throw new RuntimePolicyException("runtime.operation_id_invalid");
        await using var callback = new RuntimeAuthorizationServer<MihomoCurrentAuthorization>(executionId, authorization.ReadAsync);
        var result = await CallAsync<RuntimeSwitchResult>(new(
            restart ? PrivilegedOperationKind.MihomoRuntimeTunRestart : PrivilegedOperationKind.MihomoRuntimeTunStart,
            OperationId: executionId, RuntimeLifecycle: new(Authorization: callback.Channel, MihomoTunPublicationId: publicationId)), ct);
        if (result.OperationId != executionId) throw new RuntimeLifecycleException("runtime.operation_id_conflict");
        return result;
    }

    internal async Task<RuntimeSwitchResult> StopManagedTunUnderGateAsync(Guid operationId, Guid publicationId, CancellationToken ct)
    {
        if (operationId == Guid.Empty || publicationId == Guid.Empty || operationId == publicationId)
            throw new RuntimePolicyException("runtime.operation_id_invalid");
        var result = await CallAsync<RuntimeSwitchResult>(new(PrivilegedOperationKind.MihomoRuntimeTunStop, OperationId: operationId,
            RuntimeLifecycle: new(MihomoTunPublicationId: publicationId)), ct);
        if (result.OperationId != operationId) throw new RuntimeLifecycleException("runtime.operation_id_conflict");
        return result;
    }

    internal async Task<RuntimeSwitchResult> InstallUnderGateAsync(Guid operationId, RuntimeChangePlan plan, CancellationToken ct)
    {
        _ = MihomoRuntimeMaterial.InstallationPlanIdentity(plan);
        await using var callback = new RuntimeAuthorizationServer<MihomoInstallationAuthorization>(operationId,
            token => authorization.ReadInstallationAsync(operationId, token));
        var result = await CallAsync<RuntimeSwitchResult>(new(PrivilegedOperationKind.MihomoRuntimeExecute,
            OperationId: operationId, RuntimeLifecycle: new(Authorization: callback.Channel, FrozenPlan: plan),
            DownloadProxy: await downloadProxy.ResolveAsync(ct)), ct);
        if (result.OperationId != operationId) throw new RuntimeLifecycleException("runtime.operation_id_conflict");
        return result;
    }

    internal async Task<RuntimeSwitchResult> RemoveUnderGateAsync(Guid operationId, RuntimeChangePlan plan, CancellationToken ct)
    {
        await using var callback = new RuntimeAuthorizationServer<MihomoCurrentAuthorization>(operationId,
            token => authorization.ReadRemovalAsync(operationId, token));
        var result = await CallAsync<RuntimeSwitchResult>(new(PrivilegedOperationKind.MihomoRuntimeExecute,
            OperationId: operationId, RuntimeLifecycle: new(Authorization: callback.Channel, FrozenPlan: plan)), ct);
        if (result.OperationId != operationId) throw new RuntimeLifecycleException("runtime.operation_id_conflict");
        return result;
    }

    internal async Task<RuntimeSwitchResult?> RecoverRemovalUnderGateAsync(Guid operationId, CancellationToken ct)
    {
        var requestId = Guid.NewGuid();
        await using var callback = new RuntimeAuthorizationServer<MihomoCurrentAuthorization>(requestId,
            token => authorization.ReadRemovalAsync(operationId, token));
        var result = await CallAsync<RuntimeSwitchResult?>(new(PrivilegedOperationKind.MihomoRuntimeRecover,
            OperationId: requestId, RuntimeLifecycle: new(Authorization: callback.Channel, RecoveryOperationId: operationId)), ct, allowNull: true);
        if (result is not null && result.OperationId != operationId) throw new RuntimeLifecycleException("runtime.operation_id_conflict");
        return result;
    }

    internal async Task<RuntimeSwitchResult?> RecoverInstallationUnderGateAsync(Guid operationId, CancellationToken ct)
    {
        var requestId = Guid.NewGuid();
        await using var callback = new RuntimeAuthorizationServer<MihomoInstallationAuthorization>(requestId,
            token => authorization.ReadInstallationAsync(operationId, token));
        var result = await CallAsync<RuntimeSwitchResult?>(new(PrivilegedOperationKind.MihomoRuntimeRecover,
            OperationId: requestId, RuntimeLifecycle: new(Authorization: callback.Channel, RecoveryOperationId: operationId)), ct, allowNull: true);
        if (result is not null && result.OperationId != operationId) throw new RuntimeLifecycleException("runtime.operation_id_conflict");
        return result;
    }

    internal async Task<RuntimeSwitchResult?> RecoverConfigurationUnderGateAsync(Guid operationId, CancellationToken ct)
    {
        var requestId = Guid.NewGuid();
        await using var callback = new RuntimeAuthorizationServer<MihomoConfigurationAuthorization>(requestId,
            token => authorization.ReadConfigurationAsync(operationId, token));
        var result = await CallAsync<RuntimeSwitchResult?>(new(PrivilegedOperationKind.MihomoRuntimeRecover,
            OperationId: requestId, RuntimeLifecycle: new(Authorization: callback.Channel, RecoveryOperationId: operationId)), ct, allowNull: true);
        if (result is not null && result.OperationId != operationId) throw new RuntimeLifecycleException("runtime.operation_id_conflict");
        return result;
    }

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

    /// <summary>
    /// Records a cancellation request for one running transaction. It takes no shared gate on purpose: the
    /// running transaction holds it for its whole life, and the request must reach the Host anyway. The
    /// operation's own receipt stays the only authority on whether the request was early enough.
    /// </summary>
    public Task<RuntimeCancelRequestResult> RequestCancelAsync(Guid operationId, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new RuntimeLifecycleException("runtime.operation_id_invalid");
        return CallAsync<RuntimeCancelRequestResult>(new(PrivilegedOperationKind.MihomoRuntimeCancel,
            RuntimeLifecycle: new(CancelOperationId: operationId)), ct);
    }

    /// <summary>Emergency stop. It deliberately needs no published-credential authorization channel.</summary>
    public async Task<RuntimeRecoverySnapshot> StopRecoveryAsync(Guid operationId, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new RuntimeLifecycleException("runtime.request_invalid");
        await store.Gate.WaitAsync(ct);
        try
        {
            try { RefuseNativeActionForDirectReload(await store.ReadAsync(ct), operationId); }
            catch (Exception ex) when (ex is RuntimePolicyException or IOException or UnauthorizedAccessException)
            {
                // The Helper still verifies its own operation ID. An unreadable Server ledger
                // must not prevent emergency stop of a real native transaction.
            }
            return await CallAsync<RuntimeRecoverySnapshot>(new(PrivilegedOperationKind.MihomoRuntimeRecoveryStop,
                RuntimeLifecycle: new(RecoveryOperationId: operationId)), ct);
        }
        finally { store.Gate.Release(); }
    }

    /// <summary>
    /// Closes a transaction whose compensation already failed. The acknowledgement is part of the Helper
    /// request, so the privileged side records the same decision the operator made rather than inferring it.
    /// A close settles only the native ledger, so the Server must have no unresolved commit of its own for
    /// the same operation; otherwise the operator would strand a publication that can never be confirmed.
    /// </summary>
    public async Task<RuntimeSwitchResult> CloseRecoveryAsync(Guid operationId, bool confirmed, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new RuntimeLifecycleException("runtime.request_invalid");
        if (!confirmed) throw new RuntimeLifecycleException("runtime.recovery_confirmation_required");
        await store.Gate.WaitAsync(ct);
        try
        {
            try
            {
                var state = await store.ReadAsync(ct);
                RefuseNativeActionForDirectReload(state, operationId);
                if (state.Pending?.OperationId == operationId)
                    throw new RuntimeLifecycleException("runtime.publication_unconfirmed");
            }
            catch (RuntimePolicyException ex) { throw new RuntimeLifecycleException(ex.ProblemCode); }
            return await CallAsync<RuntimeSwitchResult>(new(PrivilegedOperationKind.MihomoRuntimeRecoveryClose,
                RuntimeLifecycle: new(RecoveryOperationId: operationId, Confirmed: true)), ct);
        }
        finally { store.Gate.Release(); }
    }

    private static void RefuseNativeActionForDirectReload(MihomoPublicationState state, Guid operationId)
    {
        if (state.DirectReload is { } direct)
            throw new RuntimeLifecycleException(direct.OperationId == operationId
                ? direct.ProblemCode : "runtime.recovery_target_conflict");
    }

    /// <summary>
    /// Places the Host-owned GEO resource set for the active revision. It downloads nothing and accepts no
    /// path: the Helper verifies its own compiled digests against the package installed beside it.
    /// </summary>
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

    public async Task<RuntimeResourceReport> PrepareResourcesAsync(CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            try
            {
                if ((await store.ReadAsync(ct)).Pending is not null)
                    throw new RuntimeLifecycleException("runtime.publication_unconfirmed");
            }
            catch (RuntimePolicyException ex) { throw new RuntimeLifecycleException(ex.ProblemCode); }
            return await CallAsync<RuntimeResourceReport>(new(PrivilegedOperationKind.MihomoRuntimeResources), ct);
        }
        finally { store.Gate.Release(); }
    }

    private Task<T> CallAsync<T>(PrivilegedOperationRequest request, CancellationToken ct, bool allowNull = false) =>
        MihomoRuntimeHelperProtocol.CallAsync<T>(helper, mode, request, ct, allowNull);
}
