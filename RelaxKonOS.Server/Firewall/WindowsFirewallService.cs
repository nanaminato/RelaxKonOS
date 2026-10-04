using System.Text.Json;
using RelaxKonOS.Protocol.Firewall;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Firewall;

/// <summary>All Windows host changes go through the authenticated privileged Helper.</summary>
public sealed class WindowsFirewallService(IPrivilegedOperationTransport transport) : IHostFirewallService
{
    public async Task<FirewallStatusDto> GetStatusAsync(CancellationToken ct)
    {
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.FirewallWindowsStatus), ct);
        return result.Success ? Read<FirewallStatusDto>(result)
            : new(false, false, "windows-defender", null, null, null, Problem(result));
    }

    public async Task<IReadOnlyList<FirewallRuleDto>> ListRulesAsync(CancellationToken ct)
    {
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.FirewallWindowsRules), ct);
        if (!result.Success) throw new FirewallRulesUnavailableException(Problem(result));
        return Read<FirewallRuleDto[]>(result);
    }

    public Task<FirewallOperationResult> SetEnabledAsync(bool enabled, CancellationToken ct) =>
        Run(new(PrivilegedOperationKind.FirewallWindowsSetEnabled, FirewallEnabled: enabled), ct);

    public Task<FirewallOperationResult> SetDefaultsAsync(string incomingPolicy, string outgoingPolicy, CancellationToken ct) =>
        Policy(incomingPolicy, out var incoming) && Policy(outgoingPolicy, out var outgoing)
            ? Run(new(PrivilegedOperationKind.FirewallWindowsSetDefaults, FirewallIncomingPolicy: incoming, FirewallOutgoingPolicy: outgoing), ct)
            : Task.FromResult(new FirewallOperationResult(false, "firewall.invalid_default_policy"));

    public Task<FirewallOperationResult> CreateRuleAsync(CreateFirewallRuleRequest request, CancellationToken ct) =>
        Rule(PrivilegedOperationKind.FirewallWindowsCreateRule, null, request.Action, request.Direction, request.Protocol, request.Source, request.Destination, request.Port, ct);
    public Task<FirewallOperationResult> UpdateRuleAsync(int number, UpdateFirewallRuleRequest request, CancellationToken ct) =>
        Rule(PrivilegedOperationKind.FirewallWindowsReplaceRule, number, request.Action, request.Direction, request.Protocol, request.Source, request.Destination, request.Port, ct);
    public Task<FirewallOperationResult> DeleteRuleAsync(int number, CancellationToken ct) => number is > 0 and <= 10_000
        ? Run(new(PrivilegedOperationKind.FirewallWindowsDeleteRule, FirewallRuleNumber: number), ct)
        : Task.FromResult(new FirewallOperationResult(false, "firewall.invalid_rule_number"));

    private Task<FirewallOperationResult> Rule(PrivilegedOperationKind operation, int? number, string action, string direction, string protocol, string source, string destination, string port, CancellationToken ct)
    {
        if (!Enum.TryParse<FirewallRuleAction>(action, true, out var a)
            || !Enum.TryParse<FirewallRuleDirection>(direction, true, out var d)
            || !Enum.TryParse<FirewallRuleProtocol>(protocol, true, out var p) || number is <= 0 or > 10_000)
            return Task.FromResult(new FirewallOperationResult(false, "firewall.invalid_rule"));
        var request = new PrivilegedOperationRequest(operation, FirewallRuleAction: a, FirewallRuleDirection: d, FirewallRuleProtocol: p,
            FirewallSource: Normalize(source), FirewallDestination: Normalize(destination), FirewallPort: Normalize(port), FirewallRuleNumber: number);
        return WindowsFirewallValidation.IsRule(request) ? Run(request, ct)
            : Task.FromResult(new FirewallOperationResult(false, "firewall.invalid_rule"));
    }
    private static string Normalize(string value) => string.IsNullOrWhiteSpace(value) ? "any" : value.Trim().ToLowerInvariant();
    private static bool Policy(string value, out FirewallDefaultPolicy policy) => Enum.TryParse(value, true, out policy) && WindowsFirewallValidation.IsPolicy(policy);
    private async Task<FirewallOperationResult> Run(PrivilegedOperationRequest request, CancellationToken ct)
    {
        var result = await transport.ExecuteAsync(request, ct);
        return new(result.Success, result.Success ? "" : Problem(result));
    }
    private static T Read<T>(PrivilegedOperationResult result) => JsonSerializer.Deserialize<T>(Convert.FromBase64String(result.OutputBase64 ?? ""))
        ?? throw new FirewallRulesUnavailableException("firewall.operation_failed");
    private static string Problem(PrivilegedOperationResult result) => result.ProblemCode switch
    {
        PrivilegedProblemCode.HelperUnavailable or PrivilegedProblemCode.AccessDenied => "firewall.privileged_proxy_required",
        PrivilegedProblemCode.InvalidRequest => "firewall.invalid_rule",
        _ => "firewall.operation_failed",
    };
}
