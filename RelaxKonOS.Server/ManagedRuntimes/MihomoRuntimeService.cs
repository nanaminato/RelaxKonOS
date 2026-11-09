using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy.Mihomo;

namespace RelaxKonOS.Server.ManagedRuntimes;

/// <summary>Reads native installation or original receipt facts without executing a runtime change.</summary>
public interface IMihomoRuntimeReceiptReader
{
    Task<RuntimeInstallationSnapshot> QueryHelperAsync(CancellationToken ct, Guid? receiptOperationId = null);
}

/// <summary>Server composition: published-state gate, per-operation authenticated callback, and installed Helper.</summary>
public sealed class MihomoRuntimeService(IPrivilegedOperationTransport helper, MihomoPublicationStore store,
    MihomoRuntimeAuthorizationProvider authorization, IServerModeResolver mode, IRuntimeDownloadProxyProvider downloadProxy)
    : IMihomoRuntimeReceiptReader
{
    /// <summary>Public observation includes unresolved Server publication work, even after root commits.
    /// It never upgrades ownership from an encrypted file or reads controller credentials.</summary>
    public async Task<RuntimeInstallationSnapshot> QueryAsync(CancellationToken ct)
    {
        var observed = await QueryHelperAsync(ct);
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

    /// <summary>Coordinators already own the Server authority; only actual Helper facts can confirm it.</summary>
    internal Task<RuntimeInstallationSnapshot> QueryHelperAsync(CancellationToken ct, Guid? receiptOperationId = null) =>
        CallAsync<RuntimeInstallationSnapshot>(new(PrivilegedOperationKind.MihomoRuntimeQuery, OperationId: receiptOperationId), ct);

    Task<RuntimeInstallationSnapshot> IMihomoRuntimeReceiptReader.QueryHelperAsync(CancellationToken ct, Guid? receiptOperationId) =>
        QueryHelperAsync(ct, receiptOperationId);

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

    internal async Task<RuntimeSwitchResult?> RecoverUnderGateAsync(Guid requestOperationId, Guid recoveryOperationId,
        CancellationToken ct, Guid? tunPublicationId = null)
    {
        await using var callback = new RuntimeAuthorizationServer<MihomoCurrentAuthorization>(requestOperationId, authorization.ReadAsync);
        // A null result proves only that no pending Helper transaction remains; callers must still query its receipt.
        return await CallAsync<RuntimeSwitchResult?>(new(PrivilegedOperationKind.MihomoRuntimeRecover,
            OperationId: requestOperationId, RuntimeLifecycle: new(Authorization: callback.Channel,
                RecoveryOperationId: recoveryOperationId, MihomoTunPublicationId: tunPublicationId)), ct, allowNull: true);
    }

    internal async Task<bool> TryRecoverNamedTunStartAsync(Guid executionId, Guid publicationId, CancellationToken ct)
    {
        // Queue reconciliation already owns its gate. A live publisher may still need to report progress there.
        if (!await store.Gate.WaitAsync(0, ct)) return false;
        try
        {
            await RecoverUnderGateAsync(Guid.NewGuid(), executionId, CancellationToken.None, publicationId);
            return true;
        }
        finally { store.Gate.Release(); }
    }

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

    private async Task<T> CallAsync<T>(PrivilegedOperationRequest request, CancellationToken ct, bool allowNull = false)
    {
        if (mode.Mode != ServerMode.System || !mode.Supports(ServerHostFeature.PrivilegedOperations))
            throw new RuntimeLifecycleException("runtime.platform_unsupported");
        var result = await helper.ExecuteAsync(request, ct);
        if (!result.Success)
        {
            var code = result.ProblemCode is PrivilegedProblemCode.HelperUnavailable or PrivilegedProblemCode.TimedOut
                ? "runtime.helper_unavailable" : PublicCode(result.Error) ? result.Error! : "runtime.helper_response_invalid";
            throw new RuntimeLifecycleException(code);
        }
        try
        {
            using var document = JsonDocument.Parse(Convert.FromBase64String(result.OutputBase64 ?? ""));
            if (typeof(T) == typeof(RuntimeInstallationSnapshot)) RuntimeCommitWitnessRules.RequireWireMember(document.RootElement, nameof(RuntimeInstallationSnapshot.CommitWitness));
            var value = document.RootElement.Deserialize<T>();
            if (value is RuntimeInstallationSnapshot witnessed && witnessed.CommitWitness is { } witness
                && !RuntimeCommitWitnessRules.BoundToSnapshot(witness, witnessed))
                throw new RuntimeLifecycleException("runtime.helper_response_invalid");
            if (value is RuntimeSwitchResult receipt && !ValidReceipt(receipt)
                || value is RuntimeInstallationSnapshot snapshot && (snapshot.Installation is null
                    || snapshot.Receipt is { } observed && (!ValidReceipt(observed) || observed.OperationId != request.OperationId)
                    || snapshot.MihomoTunAction is { } action && !ValidTunAction(action, snapshot.Receipt))
                || value is RuntimeRecoverySnapshot recovery && !ValidRecovery(recovery)
                || value is RuntimeResourceReport resources && !ValidResources(resources)
                || value is RuntimeResourceSnapshot resourceSnapshot && !ValidResourceSnapshot(resourceSnapshot))
                throw new RuntimeLifecycleException("runtime.helper_response_invalid");
            return value is not null || allowNull ? value! : throw new RuntimeLifecycleException("runtime.helper_response_invalid");
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        { throw new RuntimeLifecycleException("runtime.helper_response_invalid"); }
    }

    private static bool ValidReceipt(RuntimeSwitchResult receipt) => receipt.OperationId != Guid.Empty && Enum.IsDefined(receipt.Outcome)
        && (receipt.Outcome == RuntimeSwitchOutcome.Succeeded ? receipt.ProblemCode is null : PublicCode(receipt.ProblemCode));
    private static bool ValidTunAction(MihomoTunActionReceipt action, RuntimeSwitchResult? receipt) =>
        receipt is not null && action.OperationId == receipt.OperationId && action.Outcome == receipt.Outcome
        && action.Outcome is RuntimeSwitchOutcome.Succeeded or RuntimeSwitchOutcome.RecoveryRequired
        && action.PublicationId != Guid.Empty && action.PublicationId != action.OperationId
        && action.TargetExecutionId != Guid.Empty && action.TargetExecutionId != action.OperationId && Enum.IsDefined(action.Action);
    /// <summary>A required recovery must still name the one transaction it belongs to; open codes stay closed.</summary>
    private static bool ValidRecovery(RuntimeRecoverySnapshot recovery) =>
        (!recovery.Required || recovery.OperationId is { } id && id != Guid.Empty)
        && (recovery.Change is null || Enum.IsDefined(recovery.Change.Value))
        && (recovery.Stage is null || Enum.IsDefined(recovery.Stage.Value))
        && (recovery.ProblemCode is null || PublicCode(recovery.ProblemCode))
        && (recovery.RecoveryProblemCode is null || PublicCode(recovery.RecoveryProblemCode))
        && (recovery.ServiceProblem is null || PublicCode(recovery.ServiceProblem));
    private static bool PublicCode(string? code) => code is { Length: > 8 and <= 120 }
        && code.StartsWith("runtime.", StringComparison.Ordinal) && code.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '.');

    /// <summary>A resource report may only name the closed set the Server is willing to describe.</summary>
    private static bool ValidResources(RuntimeResourceReport report) =>
        report.RevisionId != Guid.Empty && report.Files is { } files && files.Count == MihomoGeoDataFiles.Bundled.Count
        && files.All(file => file is not null)
        && files.Select(file => file.Name).Distinct(StringComparer.Ordinal).Count() == files.Count
        && report.Files.All(file => MihomoGeoDataFiles.IsDeclared(file.Name)
            && file.Length is > 0 and <= MihomoGeoDataFiles.MaximumBytes
            && file.Sha256 is { Length: 64 } && file.Sha256.All(Uri.IsHexDigit)
            && MihomoGeoDataFiles.DigestMatches(MihomoGeoDataFiles.Bundled.Single(f => f.Name == file.Name).Sha256, file.Sha256));

    private static bool ValidResourceSnapshot(RuntimeResourceSnapshot snapshot)
    {
        if (snapshot.RevisionId == Guid.Empty || snapshot.Files is null) return false;
        if (snapshot.ProblemCode is { } problem)
            return PublicCode(problem) && snapshot.Files.Count == 0
                && (problem != "runtime.installation_absent" || snapshot.RevisionId is null);
        return snapshot.RevisionId is not null && snapshot.Files.Count == MihomoGeoDataFiles.Bundled.Count
            && snapshot.Files.All(file => file is not null)
            && snapshot.Files.Select(file => file.Name).Distinct(StringComparer.Ordinal).Count() == snapshot.Files.Count
            && snapshot.Files.All(file => MihomoGeoDataFiles.IsDeclared(file.Name) && Enum.IsDefined(file.State)
                && (file.State == RuntimeResourceState.Verified
                    ? file.ProblemCode is null && file.Length is > 0 and <= MihomoGeoDataFiles.MaximumBytes
                        && file.Sha256 is { Length: 64 } && file.Sha256.All(Uri.IsHexDigit)
                        && MihomoGeoDataFiles.DigestMatches(MihomoGeoDataFiles.Bundled.Single(f => f.Name == file.Name).Sha256, file.Sha256)
                    : file.Length is null && file.Sha256 is null && (file.State == RuntimeResourceState.Missing
                        ? file.ProblemCode is null : PublicCode(file.ProblemCode))));
    }
}
