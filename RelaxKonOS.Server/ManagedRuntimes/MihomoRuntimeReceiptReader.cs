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
    Task<MihomoRuntimeResponsibility> QueryResponsibilityAsync(CancellationToken ct);
}

internal sealed class MihomoRuntimeReceiptReader(IPrivilegedOperationTransport helper, IServerModeResolver mode) : IMihomoRuntimeReceiptReader
{
    public Task<RuntimeInstallationSnapshot> QueryHelperAsync(CancellationToken ct, Guid? receiptOperationId = null) =>
        QueryAsync(helper, mode, ct, receiptOperationId);

    public Task<MihomoRuntimeResponsibility> QueryResponsibilityAsync(CancellationToken ct) =>
        MihomoRuntimeHelperProtocol.CallAsync<MihomoRuntimeResponsibility>(helper, mode,
            new(PrivilegedOperationKind.MihomoRuntimeResponsibilityQuery), ct);

    internal static Task<RuntimeInstallationSnapshot> QueryAsync(IPrivilegedOperationTransport helper, IServerModeResolver mode,
        CancellationToken ct, Guid? receiptOperationId) =>
        MihomoRuntimeHelperProtocol.CallAsync<RuntimeInstallationSnapshot>(helper, mode,
            new(PrivilegedOperationKind.MihomoRuntimeQuery, OperationId: receiptOperationId), ct);
}

internal static class MihomoRuntimeHelperProtocol
{
    private static readonly JsonSerializerOptions ResponsibilityJson = new() { AllowDuplicateProperties = false };
    internal static async Task<RuntimeSwitchResult?> RecoverUnderGateAsync(IPrivilegedOperationTransport helper,
        IServerModeResolver mode, MihomoRuntimeAuthorizationProvider authorization, Guid requestOperationId,
        Guid recoveryOperationId, CancellationToken ct, Guid? tunPublicationId = null)
    {
        await using var callback = new RuntimeAuthorizationServer<MihomoCurrentAuthorization>(requestOperationId, authorization.ReadAsync);
        // A null response still requires checking the original Helper receipt.
        return await MihomoRuntimeHelperProtocol.CallAsync<RuntimeSwitchResult?>(helper, mode, new(PrivilegedOperationKind.MihomoRuntimeRecover,
            OperationId: requestOperationId, RuntimeLifecycle: new(Authorization: callback.Channel,
                RecoveryOperationId: recoveryOperationId, MihomoTunPublicationId: tunPublicationId)), ct, allowNull: true);
    }

    internal static async Task<T> CallAsync<T>(IPrivilegedOperationTransport helper, IServerModeResolver mode,
        PrivilegedOperationRequest request, CancellationToken ct, bool allowNull = false)
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
            var value = document.RootElement.Deserialize<T>(typeof(T) == typeof(MihomoRuntimeResponsibility) ? ResponsibilityJson : null);
            if (value is RuntimeInstallationSnapshot witnessed && witnessed.CommitWitness is { } witness
                && !RuntimeCommitWitnessRules.BoundToSnapshot(witness, witnessed))
                throw new RuntimeLifecycleException("runtime.helper_response_invalid");
            if (value is RuntimeSwitchResult receipt && !ValidReceipt(receipt)
                || value is RuntimeInstallationSnapshot snapshot && (snapshot.Installation is null
                    || snapshot.Receipt is { } observed && (!ValidReceipt(observed) || observed.OperationId != request.OperationId)
                    || snapshot.MihomoTunAction is { } action && !ValidTunAction(action, snapshot.Receipt))
                || value is RuntimeRecoverySnapshot recovery && !ValidRecovery(recovery)
                || value is MihomoRuntimeResponsibility responsibility
                    && (responsibility.PendingOperationId == Guid.Empty || responsibility.TunPublicationId == Guid.Empty)
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
