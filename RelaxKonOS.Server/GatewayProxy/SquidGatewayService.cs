using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using RelaxKonOS.Protocol.GatewayProxy;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy;

namespace RelaxKonOS.Server.GatewayProxy;

public sealed class SquidGatewayService(IPrivilegedOperationTransport helper, SquidConfigurationStore configurations)
{
    internal const string PublicationPending = "squid_publication";
    internal const string PublicationKind = "squid.configuration.apply";
    internal const string LifecyclePending = "squid_lifecycle";

    internal static bool IsTrackedLifecycle(SquidServiceAction action) =>
        Enum.IsDefined(action) && action is not (SquidServiceAction.Install or SquidServiceAction.Uninstall);

    internal static SquidServiceAction? LifecycleAction(string kind)
    {
        const string prefix = "squid.lifecycle.";
        return kind.StartsWith(prefix, StringComparison.Ordinal)
            && Enum.TryParse<SquidServiceAction>(kind.AsSpan(prefix.Length), true, out var action)
            && IsTrackedLifecycle(action) && kind == prefix + action.ToString().ToLowerInvariant() ? action : null;
    }

    internal static bool IsLifecycleUnconfirmed(string? problem) => problem is not
        (null or "squid.action.invalid" or "squid.platform.unsupported" or "squid.configuration.adoption_required"
            or "squid.configuration_directory.missing" or "squid.runtime.unavailable");

    internal static bool IsPublicationUnconfirmed(string? problem) => problem is "squid.publication.unconfirmed"
        or "squid.helper.unavailable" or "squid.recovery.required" or "squid.io_or_busy" or "squid.runtime.unavailable"
        or "squid.path.denied";

    public async Task<SquidStatus?> GetStatusAsync(CancellationToken ct)
    {
        var result = await helper.ExecuteAsync(new(PrivilegedOperationKind.SquidStatus), ct);
        if (!result.Success || result.OutputBase64 is null) return null;
        try { return JsonSerializer.Deserialize<SquidStatus>(Convert.FromBase64String(result.OutputBase64)); }
        catch (Exception ex) when (ex is JsonException or FormatException) { return null; }
    }

    public async Task<string?> ApplyAsync(Guid operationId, ApplySquidConfigurationRequest request,
        ProxyOperationStageReporter stage, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A Squid publication requires an operation ID.", nameof(operationId));
        var preview = SquidConfigurationRenderer.Preview(request.Configuration);
        if (!preview.Valid) return "squid.configuration.invalid";
        await stage(PublicationPending);
        await configurations.Gate.WaitAsync(ct);
        try
        {
            var previous = await configurations.ReadAsync(ct);
            if (IsPublicationUnconfirmed(previous.LastProblemCode)) return "squid.recovery.required";
            var pending = previous with { Configuration = request.Configuration, DesiredRevision = checked(previous.DesiredRevision + 1),
                LastProblemCode = "squid.publication.unconfirmed", PublicationOperationId = operationId };
            // Persist intent before invoking the Helper. An interruption must not look published.
            await configurations.WriteAsync(pending, ct);
            var result = await helper.ExecuteAsync(new(PrivilegedOperationKind.SquidApplyConfiguration,
                OperationId: operationId, SquidConfiguration: request), ct);
            if (result.Success || result.Error == RolledBack)
            {
                if (result.SquidPublicationReceipt is not { } receipt || result.Success != receipt.Applied
                    || !Matches(pending, receipt)) return "squid.recovery.required";
                await ConfirmAsync(pending, receipt);
                return receipt.Applied ? null : RolledBack;
            }
            var problem = Problem(result);
            await configurations.WriteAsync(pending with { LastProblemCode = problem }, CancellationToken.None);
            return problem;
        }
        finally { configurations.Gate.Release(); }
    }

    internal const string RolledBack = "squid.apply.failed_rolled_back";

    internal async Task<SquidPublicationReceipt?> RecoverPublicationAsync(Guid operationId, CancellationToken ct)
    {
        await configurations.Gate.WaitAsync(ct);
        try
        {
            var state = await configurations.ReadAsync(ct);
            if (state.PublicationOperationId != operationId) return null;
            var result = await helper.ExecuteAsync(new(PrivilegedOperationKind.SquidConfigurationReceipt, OperationId: operationId), CancellationToken.None);
            if (result is not { Success: true, SquidPublicationReceipt: { } receipt } || !Matches(state, receipt)) return null;
            await ConfirmAsync(state, receipt);
            return receipt;
        }
        finally { configurations.Gate.Release(); }
    }

    private static bool Matches(SquidConfigurationState state, SquidPublicationReceipt receipt)
    {
        var preview = SquidConfigurationRenderer.Preview(state.Configuration);
        return receipt.OperationId != Guid.Empty && receipt.OperationId == state.PublicationOperationId && preview.Valid
            && receipt.CandidateHash == Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(preview.Configuration!)))
            && receipt.ObservedHash is { Length: 64 } hash && hash.All(Uri.IsHexDigit)
            && (!receipt.Applied || receipt.ObservedHash == receipt.CandidateHash);
    }

    private async Task ConfirmAsync(SquidConfigurationState state, SquidPublicationReceipt receipt)
    {
        var confirmed = receipt.Applied ? state with { PublishedRevision = state.DesiredRevision,
            PublishedConfigurationHash = receipt.CandidateHash, LastProblemCode = null }
            : state with { LastProblemCode = RolledBack };
        if (confirmed != state) await configurations.WriteAsync(confirmed, CancellationToken.None);
    }

    public async Task<SquidConfigurationSnapshot?> GetConfigurationAsync(CancellationToken ct)
    {
        await configurations.Gate.WaitAsync(ct);
        try
        {
            var state = await configurations.ReadAsync(ct);
            var status = await GetStatusAsync(ct);
            return status is null ? null : new(state, status, status.Managed && !status.RecoveryRequired
                && state.PublishedConfigurationHash is not null
                && string.Equals(state.PublishedConfigurationHash, status.ConfigurationRevision, StringComparison.Ordinal));
        }
        finally { configurations.Gate.Release(); }
    }

    public async Task<SquidNativeValidation> ValidateAsync(SquidConfiguration configuration, CancellationToken ct)
    {
        if (!SquidConfigurationRenderer.Preview(configuration).Valid) return new(false, "squid.configuration.invalid", "");
        var result = await helper.ExecuteAsync(new(PrivilegedOperationKind.SquidValidateConfiguration, SquidConfiguration: new(configuration)), ct);
        string diagnostics;
        try { diagnostics = result.OutputBase64 is null ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(result.OutputBase64)); }
        catch (FormatException) { return new(false, "squid.helper.invalid_response", ""); }
        return new(result.Success, Problem(result), diagnostics.Length > 16384 ? diagnostics[..16384] : diagnostics);
    }

    public async Task<string?> ExecuteAsync(Guid id, SquidServiceAction action, ProxyOperationStageReporter stage, CancellationToken ct)
    {
        if (!Enum.IsDefined(action)) return "squid.action.invalid";
        var tracked = IsTrackedLifecycle(action);
        if (tracked)
        {
            if (id == Guid.Empty) throw new ArgumentException("A Squid lifecycle action requires an operation ID.", nameof(id));
            await stage(LifecyclePending);
        }
        var result = await helper.ExecuteAsync(new(
            tracked ? PrivilegedOperationKind.SquidTrackedLifecycle : PrivilegedOperationKind.SquidServiceAction,
            OperationId: tracked ? id : null, SquidServiceAction: action), ct);
        if (result.Success) return !tracked || MatchesLifecycle(result, id, action) ? null : "squid.recovery.required";
        if (tracked && result.Error == "squid.runtime.unavailable" && result.ProblemCode != PrivilegedProblemCode.UnsupportedOperation)
            return "squid.recovery.required";
        // The Helper attaches the unit journal to a failed lifecycle action; only a message the host
        // actually printed may replace the generic "the daemon exited" code.
        var classified = SquidFailureClassifier.Classify(Evidence(result));
        return classified != SquidFailureClassifier.ProcessFailed ? classified : Problem(result);
    }

    internal async Task<bool> RecoverLifecycleAsync(Guid id, SquidServiceAction action)
    {
        var result = await helper.ExecuteAsync(new(PrivilegedOperationKind.SquidLifecycleReceipt,
            OperationId: id, SquidServiceAction: action), CancellationToken.None);
        return MatchesLifecycle(result, id, action);
    }

    private static bool MatchesLifecycle(PrivilegedOperationResult result, Guid id, SquidServiceAction action)
    {
        if (!result.Success || result.OutputBase64 is not { } output) return false;
        try { return JsonSerializer.Deserialize<SquidLifecycleReceipt>(Convert.FromBase64String(output)) == new SquidLifecycleReceipt(id, action); }
        catch (Exception error) when (error is JsonException or FormatException) { return false; }
    }

    public async Task<SquidAccount[]?> GetAccountsAsync(CancellationToken ct)
    {
        var result = await helper.ExecuteAsync(new(PrivilegedOperationKind.SquidAccountsRead), ct);
        if (!result.Success || result.OutputBase64 is null) return null;
        try { return JsonSerializer.Deserialize<SquidAccount[]>(Convert.FromBase64String(result.OutputBase64)); }
        catch (Exception ex) when (ex is JsonException or FormatException) { return null; }
    }

    public async Task<string?> MutateAccountAsync(SquidAccountMutation request, CancellationToken ct)
    {
        if (!SquidAccountValidation.Valid(request)) return "squid.accounts.invalid";
        await configurations.Gate.WaitAsync(ct);
        try { return Problem(await helper.ExecuteAsync(new(PrivilegedOperationKind.SquidAccountMutate, SquidAccount: request), ct)); }
        finally { configurations.Gate.Release(); }
    }

    /// <summary>The Helper's attached unit output, or nothing when it is absent or not base64.</summary>
    private static string? Evidence(PrivilegedOperationResult result)
    {
        if (result.OutputBase64 is not { Length: > 0 } base64) return null;
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(base64)); }
        catch (FormatException) { return null; }
    }

    private static string? Problem(PrivilegedOperationResult result) => result.Success ? null :
        result.Error is { } error && error.StartsWith("squid.", StringComparison.Ordinal) ? error : "squid.helper.unavailable";
}
