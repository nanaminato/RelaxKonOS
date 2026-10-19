using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.PrivilegedHelper;

internal static class MihomoRuntimeOperations
{
    internal static async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request,
        Func<MihomoRuntimeLifecycle>? create = null, Func<PrivilegedOperationFrame, Task>? report = null)
    {
        var clean = new PrivilegedOperationRequest(request.Operation, OperationId: request.OperationId,
            Correlation: request.Correlation, Version: request.Version, RuntimeLifecycle: request.RuntimeLifecycle);
        var input = request.RuntimeLifecycle;
        if (input?.ConfigurationPlan is not null && request.Operation != PrivilegedOperationKind.MihomoRuntimeConfigure)
            return new(false, 64, ProblemCode: PrivilegedProblemCode.InvalidRequest, Error: "runtime.request_invalid");
        var valid = request.Operation switch
        {
            PrivilegedOperationKind.MihomoRuntimeQuery => input is null,
            PrivilegedOperationKind.MihomoRuntimeConfigure => input is { SourceId: null, Plan: null, Authorization: not null,
                FrozenPlan: null, Confirmed: false, RecoveryOperationId: null, CancelOperationId: null, ConfigurationPlan: { RuntimeId: "mihomo" } },
            PrivilegedOperationKind.MihomoRuntimePlan => input is { Plan: not null, Authorization: null, FrozenPlan: null, Confirmed: false, RecoveryOperationId: null, CancelOperationId: null }
                && RuntimeValidation.Identifier(input.SourceId),
            PrivilegedOperationKind.MihomoRuntimeExecute => input is { SourceId: null, Plan: null, Authorization: not null, Confirmed: false, FrozenPlan: { RuntimeId: "mihomo", Request: not null }, RecoveryOperationId: null, CancelOperationId: null }
                && (input.FrozenPlan.Release is not null || input.FrozenPlan.Request.Change == RuntimeChange.Uninstall),
            PrivilegedOperationKind.MihomoRuntimeRecover => input is { SourceId: null, Plan: null, Authorization: not null, FrozenPlan: null, Confirmed: false, RecoveryOperationId: { } target, CancelOperationId: null }
                && target != Guid.Empty,
            PrivilegedOperationKind.MihomoRuntimeCancel => input is { SourceId: null, Plan: null, Authorization: null, FrozenPlan: null, Confirmed: false, RecoveryOperationId: null, CancelOperationId: { } cancelTarget }
                && cancelTarget != Guid.Empty,
            PrivilegedOperationKind.MihomoRuntimeRecoveryQuery => input is null,
            PrivilegedOperationKind.MihomoRuntimeRecoveryStop => input is { SourceId: null, Plan: null, Authorization: null, FrozenPlan: null, Confirmed: false, RecoveryOperationId: { } stopTarget, CancelOperationId: null }
                && stopTarget != Guid.Empty,
            PrivilegedOperationKind.MihomoRuntimeRecoveryClose => input is { SourceId: null, Plan: null, Authorization: null, FrozenPlan: null, Confirmed: true, RecoveryOperationId: { } closeTarget, CancelOperationId: null }
                && closeTarget != Guid.Empty,
            // The resource set is closed and compiled into the Helper, so the request carries no selection.
            PrivilegedOperationKind.MihomoRuntimeResources => input is null,
            _ => false
        };
        if (clean != request || !valid || request.OperationId is not { } id || id == Guid.Empty
            || input?.Authorization is { } channel && (channel.OperationId != id || channel.ChannelId == Guid.Empty
                || channel.RuntimeId != "mihomo"
                || channel.Secret is not { Length: 64 } || !channel.Secret.All(Uri.IsHexDigit)))
            return new(false, 64, ProblemCode: PrivilegedProblemCode.InvalidRequest, Error: "runtime.request_invalid");
        if (!OperatingSystem.IsLinux())
            return new(false, 64, ProblemCode: PrivilegedProblemCode.UnsupportedOperation, Error: "runtime.platform_unsupported");
        try
        {
            var lifecycle = (create ?? MihomoRuntimeLifecycle.Create)();
            RuntimeStageReporter? progress = report is null ? null : (stage, cancellable) => report(PrivilegedOperationFrame.Report(stage, cancellable: cancellable));
            return request.Operation switch
            {
                PrivilegedOperationKind.MihomoRuntimeQuery => Json(await lifecycle.QueryAsync(default, id)),
                PrivilegedOperationKind.MihomoRuntimeConfigure => Json(await lifecycle.ConfigureAsync(id, input!.ConfigurationPlan!, input.Authorization!, default, progress)),
                PrivilegedOperationKind.MihomoRuntimePlan => Json(await lifecycle.PlanAsync(input!.SourceId!, input.Plan!, default)),
                PrivilegedOperationKind.MihomoRuntimeExecute => Json(await lifecycle.ExecuteAsync(id, input!.FrozenPlan!, input.Authorization!, default, progress)),
                PrivilegedOperationKind.MihomoRuntimeRecover => Json(await lifecycle.RecoverAsync(input!.Authorization!, input.RecoveryOperationId!.Value, default, progress)),
                PrivilegedOperationKind.MihomoRuntimeCancel => Json(lifecycle.CancelRequest(input!.CancelOperationId!.Value)),
                PrivilegedOperationKind.MihomoRuntimeRecoveryQuery => Json(await lifecycle.RecoveryAsync(default)),
                PrivilegedOperationKind.MihomoRuntimeRecoveryStop => Json(await lifecycle.StopAsync(input!.RecoveryOperationId!.Value, default)),
                PrivilegedOperationKind.MihomoRuntimeResources => Json(await lifecycle.PrepareResourcesAsync(default)),
                _ => Json(await lifecycle.CloseAsync(input!.RecoveryOperationId!.Value, default))
            };
        }
        catch (RuntimePolicyException ex) { return new(false, 1, Error: ex.ProblemCode, ProblemCode: PrivilegedProblemCode.Conflict); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
            or JsonException or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException)
        { return new(false, 1, Error: "runtime.state_unknown", ProblemCode: PrivilegedProblemCode.InternalError); }
    }

    private static PrivilegedOperationResult Json<T>(T value) => new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)));
}
