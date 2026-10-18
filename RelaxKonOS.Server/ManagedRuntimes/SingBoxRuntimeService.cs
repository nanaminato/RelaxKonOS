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
    ProxyServerRuntimeAuthorizationProvider authorization, IServerModeResolver mode)
{
    public Task<RuntimeInstallationSnapshot> QueryAsync(CancellationToken ct, Guid? receiptOperationId = null) =>
        CallAsync<RuntimeInstallationSnapshot>(new(PrivilegedOperationKind.SingBoxRuntimeQuery, OperationId: receiptOperationId), ct);

    public async Task<RuntimePlanResult> PlanAsync(string sourceId, RuntimePlanRequest request, CancellationToken ct)
    {
        if (!RuntimeValidation.Identifier(sourceId)) throw new RuntimeLifecycleException("runtime.source_invalid");
        await store.Gate.WaitAsync(ct);
        try
        {
            return await CallAsync<RuntimePlanResult>(new(PrivilegedOperationKind.SingBoxRuntimePlan,
                RuntimeLifecycle: new(SourceId: sourceId, Plan: request)), ct);
        }
        finally { store.Gate.Release(); }
    }

    public async Task<RuntimeSwitchResult> ExecuteAsync(Guid operationId, RuntimeChangePlan plan, CancellationToken ct)
    {
        if (operationId == Guid.Empty || plan is null || plan.RuntimeId != "sing-box" || plan.Release is null)
            throw new RuntimeLifecycleException("runtime.request_invalid");
        await store.Gate.WaitAsync(ct);
        try
        {
            await using var callback = new RuntimeAuthorizationServer(operationId, authorization.ReadAsync);
            var result = await CallAsync<RuntimeSwitchResult>(new(PrivilegedOperationKind.SingBoxRuntimeExecute,
                OperationId: operationId, RuntimeLifecycle: new(Authorization: callback.Channel, FrozenPlan: plan)), CancellationToken.None);
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
            await using var callback = new RuntimeAuthorizationServer(requestOperationId, authorization.ReadAsync);
            // A null result proves only that no pending Helper transaction remains; callers must still query its receipt.
            return await CallAsync<RuntimeSwitchResult?>(new(PrivilegedOperationKind.SingBoxRuntimeRecover,
                OperationId: requestOperationId, RuntimeLifecycle: new(Authorization: callback.Channel, RecoveryOperationId: recoveryOperationId)), CancellationToken.None, allowNull: true);
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
                    || snapshot.Receipt is { } observed && (!ValidReceipt(observed) || observed.OperationId != request.OperationId)))
                throw new RuntimeLifecycleException("runtime.helper_response_invalid");
            return value is not null || allowNull ? value! : throw new RuntimeLifecycleException("runtime.helper_response_invalid");
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        { throw new RuntimeLifecycleException("runtime.helper_response_invalid"); }
    }

    private static bool ValidReceipt(RuntimeSwitchResult receipt) => receipt.OperationId != Guid.Empty && Enum.IsDefined(receipt.Outcome)
        && (receipt.Outcome == RuntimeSwitchOutcome.Succeeded ? receipt.ProblemCode is null : PublicCode(receipt.ProblemCode));
    private static bool PublicCode(string? code) => code is { Length: > 8 and <= 120 }
        && code.StartsWith("runtime.", StringComparison.Ordinal) && code.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '.');
}
