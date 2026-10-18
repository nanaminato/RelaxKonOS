using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.PrivilegedHelper;

internal static class SingBoxRuntimeOperations
{
    internal static async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request,
        Func<SingBoxRuntimeLifecycle>? create = null, Func<PrivilegedOperationFrame, Task>? report = null)
    {
        var clean = new PrivilegedOperationRequest(request.Operation, OperationId: request.OperationId,
            Correlation: request.Correlation, Version: request.Version, RuntimeLifecycle: request.RuntimeLifecycle);
        var input = request.RuntimeLifecycle;
        var valid = request.Operation switch
        {
            PrivilegedOperationKind.SingBoxRuntimeQuery => input is null,
            PrivilegedOperationKind.SingBoxRuntimePlan => input is { Plan: not null, Authorization: null, FrozenPlan: null, RecoveryOperationId: null }
                && RuntimeValidation.Identifier(input.SourceId),
            PrivilegedOperationKind.SingBoxRuntimeExecute => input is { SourceId: null, Plan: null, Authorization: not null, FrozenPlan: { Request: not null, Release: not null }, RecoveryOperationId: null },
            PrivilegedOperationKind.SingBoxRuntimeRecover => input is { SourceId: null, Plan: null, Authorization: not null, FrozenPlan: null, RecoveryOperationId: { } target }
                && target != Guid.Empty,
            _ => false
        };
        if (clean != request || !valid || request.OperationId is not { } id || id == Guid.Empty
            || input?.Authorization is { } channel && (channel.OperationId != id || channel.ChannelId == Guid.Empty
                || channel.Secret is not { Length: 64 } || !channel.Secret.All(Uri.IsHexDigit)))
            return new(false, 64, ProblemCode: PrivilegedProblemCode.InvalidRequest, Error: "runtime.request_invalid");
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return new(false, 64, ProblemCode: PrivilegedProblemCode.UnsupportedOperation, Error: "runtime.platform_unsupported");
        try
        {
            var lifecycle = (create ?? SingBoxRuntimeLifecycle.Create)();
            Func<InstallationStage, Task>? progress = report is null ? null : stage => report(PrivilegedOperationFrame.Report(stage));
            return request.Operation switch
            {
                PrivilegedOperationKind.SingBoxRuntimeQuery => Json(await lifecycle.QueryAsync(default, id)),
                PrivilegedOperationKind.SingBoxRuntimePlan => Json(await lifecycle.PlanAsync(input!.SourceId!, input.Plan!, default)),
                PrivilegedOperationKind.SingBoxRuntimeExecute => Json(await lifecycle.ExecuteAsync(id, input!.FrozenPlan!, input.Authorization!, default, progress)),
                _ => Json(await lifecycle.RecoverAsync(input!.Authorization!, input.RecoveryOperationId!.Value, default, progress))
            };
        }
        catch (RuntimePolicyException ex) { return new(false, 1, Error: ex.ProblemCode, ProblemCode: PrivilegedProblemCode.Conflict); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
            or JsonException or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException)
        { return new(false, 1, Error: "runtime.state_unknown", ProblemCode: PrivilegedProblemCode.InternalError); }
    }

    private static PrivilegedOperationResult Json<T>(T value) => new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)));
}
