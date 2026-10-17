using System.Text.Json;
using System.Text;
using RelaxKonOS.Protocol.GatewayProxy;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.GatewayProxy;

public sealed class SquidGatewayService(IPrivilegedOperationTransport helper, SquidConfigurationStore configurations)
{
    public async Task<SquidStatus?> GetStatusAsync(CancellationToken ct)
    {
        var result = await helper.ExecuteAsync(new(PrivilegedOperationKind.SquidStatus), ct);
        if (!result.Success || result.OutputBase64 is null) return null;
        try { return JsonSerializer.Deserialize<SquidStatus>(Convert.FromBase64String(result.OutputBase64)); }
        catch (Exception ex) when (ex is JsonException or FormatException) { return null; }
    }

    public async Task<string?> ApplyAsync(ApplySquidConfigurationRequest request, CancellationToken ct)
    {
        var preview = SquidConfigurationRenderer.Preview(request.Configuration);
        if (!preview.Valid) return "squid.configuration.invalid";
        await configurations.Gate.WaitAsync(ct);
        try
        {
            var previous = await configurations.ReadAsync(ct);
            var pending = previous with { Configuration = request.Configuration, DesiredRevision = checked(previous.DesiredRevision + 1), LastProblemCode = "squid.publication.unconfirmed" };
            // Persist intent before invoking the Helper. An interruption must not look published.
            await configurations.WriteAsync(pending, ct);
            var result = await helper.ExecuteAsync(new(PrivilegedOperationKind.SquidApplyConfiguration, SquidConfiguration: request), ct);
            var problem = Problem(result);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(preview.Configuration!))).ToLowerInvariant();
            await configurations.WriteAsync(problem is null
                ? pending with { PublishedRevision = pending.DesiredRevision, PublishedConfigurationHash = hash, LastProblemCode = null }
                : pending with { LastProblemCode = problem }, CancellationToken.None);
            return problem;
        }
        finally { configurations.Gate.Release(); }
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

    public async Task<string?> ExecuteAsync(SquidServiceAction action, CancellationToken ct)
    {
        if (!Enum.IsDefined(action)) return "squid.action.invalid";
        return Problem(await helper.ExecuteAsync(new(PrivilegedOperationKind.SquidServiceAction, SquidServiceAction: action), ct));
    }

    private static string? Problem(PrivilegedOperationResult result) => result.Success ? null :
        result.Error is { } error && error.StartsWith("squid.", StringComparison.Ordinal) ? error : "squid.helper.unavailable";
}
