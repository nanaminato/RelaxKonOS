using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.ProxyServer;

namespace RelaxKonOS.Server.ManagedRuntimes;

public sealed class RuntimeLifecycleException(string code) : Exception(code)
{
    public string ProblemCode { get; } = code;
}

/// <summary>Server composition: published-state gate, per-operation authenticated callback, and installed Helper.</summary>
public sealed class SingBoxRuntimeService(IPrivilegedOperationTransport helper, ProxyServerStore store,
    ProxyServerRuntimeAuthorizationProvider authorization, IServerModeResolver mode, IRuntimeDownloadProxyProvider downloadProxy)
{
    public Task<RuntimeInstallationSnapshot> QueryAsync(CancellationToken ct, Guid? receiptOperationId = null) =>
        CallAsync<RuntimeInstallationSnapshot>(new(PrivilegedOperationKind.SingBoxRuntimeQuery, OperationId: receiptOperationId), ct);

    public async Task<RuntimePlanResult> PlanAsync(string? sourceId, RuntimePlanRequest request, CancellationToken ct)
    {
        // Removal must stay admissible when the source has expired, been revoked, or is gone.
        var id = request.Change == RuntimeChange.Uninstall && string.IsNullOrWhiteSpace(sourceId) ? "removal" : sourceId;
        if (!RuntimeValidation.Identifier(id)) throw new RuntimeLifecycleException("runtime.source_invalid");
        await store.Gate.WaitAsync(ct);
        try
        {
            return await CallAsync<RuntimePlanResult>(new(PrivilegedOperationKind.SingBoxRuntimePlan,
                RuntimeLifecycle: new(SourceId: id, Plan: request)), ct);
        }
        finally { store.Gate.Release(); }
    }

    public async Task<RuntimeSwitchResult> ExecuteAsync(Guid operationId, RuntimeChangePlan plan, CancellationToken ct)
    {
        if (operationId == Guid.Empty || plan is null || plan.Request is null || plan.RuntimeId != "sing-box")
            throw new RuntimeLifecycleException("runtime.request_invalid");
        // Removal carries no release target; every other change must name the exact frozen artifact.
        var removal = plan.Request.Change == RuntimeChange.Uninstall;
        if (removal ? plan.Release is not null || plan.Request.ReleaseId is not null : plan.Release is null)
            throw new RuntimeLifecycleException("runtime.request_invalid");
        await store.Gate.WaitAsync(ct);
        try
        {
            await using var callback = new RuntimeAuthorizationServer<SingBoxCurrentAuthorization>(operationId, authorization.ReadAsync);
            var result = await CallAsync<RuntimeSwitchResult>(new(PrivilegedOperationKind.SingBoxRuntimeExecute,
                OperationId: operationId, RuntimeLifecycle: new(Authorization: callback.Channel, FrozenPlan: plan),
                DownloadProxy: removal ? null : await downloadProxy.ResolveAsync(ct)), CancellationToken.None);
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
            await using var callback = new RuntimeAuthorizationServer<SingBoxCurrentAuthorization>(requestOperationId, authorization.ReadAsync);
            // A null result proves only that no pending Helper transaction remains; callers must still query its receipt.
            return await CallAsync<RuntimeSwitchResult?>(new(PrivilegedOperationKind.SingBoxRuntimeRecover,
                OperationId: requestOperationId, RuntimeLifecycle: new(Authorization: callback.Channel, RecoveryOperationId: recoveryOperationId)), CancellationToken.None, allowNull: true);
        }
        finally { store.Gate.Release(); }
    }

    /// <summary>Read-only recovery facts. It observes the service but changes neither the ledger nor the service.</summary>
    public Task<RuntimeRecoverySnapshot> RecoveryAsync(CancellationToken ct) =>
        CallAsync<RuntimeRecoverySnapshot>(new(PrivilegedOperationKind.SingBoxRuntimeRecoveryQuery), ct);

    /// <summary>
    /// Records a cancellation request for one running transaction. It takes no shared gate on purpose: the
    /// running transaction holds it for its whole life, and the request must reach the Host anyway. The
    /// operation's own receipt stays the only authority on whether the request was early enough.
    /// </summary>
    public Task<RuntimeCancelRequestResult> RequestCancelAsync(Guid operationId, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new RuntimeLifecycleException("runtime.operation_id_invalid");
        return CallAsync<RuntimeCancelRequestResult>(new(PrivilegedOperationKind.SingBoxRuntimeCancel,
            RuntimeLifecycle: new(CancelOperationId: operationId)), ct);
    }

    /// <summary>Emergency stop. It deliberately needs no published-credential authorization channel.</summary>
    public async Task<RuntimeRecoverySnapshot> StopRecoveryAsync(Guid operationId, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new RuntimeLifecycleException("runtime.request_invalid");
        await store.Gate.WaitAsync(ct);
        try
        {
            return await CallAsync<RuntimeRecoverySnapshot>(new(PrivilegedOperationKind.SingBoxRuntimeRecoveryStop,
                RuntimeLifecycle: new(RecoveryOperationId: operationId)), ct);
        }
        finally { store.Gate.Release(); }
    }

    /// <summary>
    /// Closes a transaction whose compensation already failed. The acknowledgement is part of the Helper
    /// request, so the privileged side records the same decision the operator made rather than inferring it.
    /// </summary>
    public async Task<RuntimeSwitchResult> CloseRecoveryAsync(Guid operationId, bool confirmed, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new RuntimeLifecycleException("runtime.request_invalid");
        if (!confirmed) throw new RuntimeLifecycleException("runtime.recovery_confirmation_required");
        await store.Gate.WaitAsync(ct);
        try
        {
            return await CallAsync<RuntimeSwitchResult>(new(PrivilegedOperationKind.SingBoxRuntimeRecoveryClose,
                RuntimeLifecycle: new(RecoveryOperationId: operationId, Confirmed: true)), ct);
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
            var value = JsonSerializer.Deserialize<T>(Convert.FromBase64String(result.OutputBase64 ?? ""));
            if (value is RuntimeSwitchResult receipt && !ValidReceipt(receipt)
                || value is RuntimeInstallationSnapshot snapshot && (snapshot.Installation is null
                    || snapshot.Receipt is { } observed && (!ValidReceipt(observed) || observed.OperationId != request.OperationId))
                || value is RuntimeRecoverySnapshot recovery && !ValidRecovery(recovery))
                throw new RuntimeLifecycleException("runtime.helper_response_invalid");
            return value is not null || allowNull ? value! : throw new RuntimeLifecycleException("runtime.helper_response_invalid");
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        { throw new RuntimeLifecycleException("runtime.helper_response_invalid"); }
    }

    private static bool ValidReceipt(RuntimeSwitchResult receipt) => receipt.OperationId != Guid.Empty && Enum.IsDefined(receipt.Outcome)
        && (receipt.Outcome == RuntimeSwitchOutcome.Succeeded ? receipt.ProblemCode is null : PublicCode(receipt.ProblemCode));
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
}
