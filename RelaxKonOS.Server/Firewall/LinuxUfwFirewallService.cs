using System.Net;
using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.Firewall;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Firewall;

/// <summary>Linux-only, injection-safe facade over the root-owned UFW helper.</summary>
public sealed class LinuxUfwFirewallService : IHostFirewallService
{
    private static readonly Regex NumberedRule = new(
        @"^\[\s*(?<number>\d+)\]\s+(?<target>.+?)\s+(?<action>ALLOW|DENY|REJECT|LIMIT)\s+(?<direction>IN|OUT)\s+(?<source>.+?)(?:\s+#.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly ILogger<LinuxUfwFirewallService> _logger;
    private readonly IPrivilegedOperationTransport _transport;

    public LinuxUfwFirewallService(IPrivilegedOperationTransport transport, ILogger<LinuxUfwFirewallService> logger)
    {
        _logger = logger;
        _transport = transport;
    }

    public async Task<FirewallStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        var status = await ExecuteAsync(new(PrivilegedOperationKind.FirewallUfwStatus), cancellationToken);
        if (!status.Success) return new(false, false, "ufw", null, null, null, status.ProblemCode);

        var enabled = status.Output.Contains("Status: active", StringComparison.OrdinalIgnoreCase);
        var defaults = Regex.Match(status.Output, @"Default:\s+(?<incoming>\w+)\s+\(incoming\),\s+(?<outgoing>\w+)\s+\(outgoing\)", RegexOptions.IgnoreCase);
        return new(true, enabled, "ufw", null,
            defaults.Success ? defaults.Groups["incoming"].Value.ToLowerInvariant() : null,
            defaults.Success ? defaults.Groups["outgoing"].Value.ToLowerInvariant() : null);
    }

    public async Task<IReadOnlyList<FirewallRuleDto>> ListRulesAsync(CancellationToken cancellationToken)
    {
        var result = await ReadRulesAsync(cancellationToken);
        if (!result.Success) return [];

        // UFW expands an any-to-any rule into adjacent IPv4 and IPv6 entries.
        // They remain one logical rule for mutation, while its address-family
        // coverage is exposed so users can see why otherwise matching rules differ.
        return result.Rules
            .Where(rule => !rule.IsIpv6 || !HasCompanion(result.Rules, rule))
            .Select(rule => rule.Rule with
            {
                AddressFamily = HasCompanion(result.Rules, rule)
                    ? "IPv4 + IPv6"
                    : rule.IsIpv6 ? "IPv6" : "IPv4",
            })
            .ToArray();
    }

    public Task<FirewallOperationResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken) =>
        ExecuteOperationAsync(new(PrivilegedOperationKind.FirewallUfwSetEnabled, FirewallEnabled: enabled), cancellationToken);

    public async Task<FirewallOperationResult> SetDefaultsAsync(string incomingPolicy, string outgoingPolicy, CancellationToken cancellationToken)
    {
        if (!IsPolicy(incomingPolicy) || !IsPolicy(outgoingPolicy)) return new(false, "firewall.invalid_default_policy");
        return await ExecuteOperationAsync(new(PrivilegedOperationKind.FirewallUfwSetDefaults,
            FirewallIncomingPolicy: ToDefaultPolicy(incomingPolicy), FirewallOutgoingPolicy: ToDefaultPolicy(outgoingPolicy)), cancellationToken);
    }

    public Task<FirewallOperationResult> CreateRuleAsync(CreateFirewallRuleRequest request, CancellationToken cancellationToken)
    {
        if (!TryValidateRule(request.Action, request.Direction, request.Protocol, request.Source, request.Destination, request.Port, out var args, out var problem)) return Task.FromResult(new FirewallOperationResult(false, problem));
        return ExecuteOperationAsync(ToRuleRequest(PrivilegedOperationKind.FirewallUfwCreateRule, args), cancellationToken);
    }

    public async Task<FirewallOperationResult> UpdateRuleAsync(int number, UpdateFirewallRuleRequest request, CancellationToken cancellationToken)
    {
        if (number is <= 0 or > 10_000) return new(false, "firewall.invalid_rule_number");
        if (!TryValidateRule(request.Action, request.Direction, request.Protocol, request.Source, request.Destination, request.Port, out var args, out var problem)) return new(false, problem);
        var numbers = await GetRuleNumbersAsync(number, cancellationToken);
        if (!numbers.Success) return new(false, numbers.ProblemCode);
        return await ExecuteOperationAsync(ToRuleRequest(PrivilegedOperationKind.FirewallUfwReplaceRule, args, numbers), cancellationToken);
    }

    public async Task<FirewallOperationResult> DeleteRuleAsync(int number, CancellationToken cancellationToken)
    {
        if (number is <= 0 or > 10_000) return new(false, "firewall.invalid_rule_number");
        var numbers = await GetRuleNumbersAsync(number, cancellationToken);
        return !numbers.Success
            ? new(false, numbers.ProblemCode)
            : await ExecuteOperationAsync(new(PrivilegedOperationKind.FirewallUfwDeleteRule,
                FirewallRuleNumber: int.Parse(numbers.Numbers[0], System.Globalization.CultureInfo.InvariantCulture),
                FirewallCompanionRuleNumber: numbers.Numbers[1] == "none" ? null : int.Parse(numbers.Numbers[1], System.Globalization.CultureInfo.InvariantCulture)), cancellationToken);
    }

    private async Task<ParsedRuleList> ReadRulesAsync(CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(new(PrivilegedOperationKind.FirewallUfwStatus, FirewallNumberedStatus: true), cancellationToken);
        if (!result.Success) return new([], result.ProblemCode);

        var rules = new List<ParsedFirewallRule>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var match = NumberedRule.Match(line);
            if (!match.Success || !int.TryParse(match.Groups["number"].Value, out var number)) continue;
            var target = ParseTarget(match.Groups["target"].Value);
            var source = NormalizeStatusEndpoint(match.Groups["source"].Value, out var sourceIsIpv6);
            rules.Add(new ParsedFirewallRule(new FirewallRuleDto(number, match.Groups["action"].Value.ToLowerInvariant(),
                match.Groups["direction"].Value.ToLowerInvariant(), target.Protocol, source, target.Destination, target.Port),
                target.IsIpv6 || sourceIsIpv6));
        }
        return new(rules, string.Empty);
    }

    private async Task<RuleNumbersResult> GetRuleNumbersAsync(int number, CancellationToken cancellationToken)
    {
        var result = await ReadRulesAsync(cancellationToken);
        if (!result.Success) return new([], result.ProblemCode);
        var selected = result.Rules.FirstOrDefault(rule => rule.Rule.Number == number);
        if (selected is null) return new([], "firewall.invalid_rule_number");

        var companion = GetCompanion(result.Rules, selected);
        var primary = companion is null ? selected.Rule.Number : Math.Min(selected.Rule.Number, companion.Rule.Number);
        var secondary = companion is null ? "none" : Math.Max(selected.Rule.Number, companion.Rule.Number).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new([primary.ToString(System.Globalization.CultureInfo.InvariantCulture), secondary], string.Empty);
    }

    private static bool HasCompanion(IReadOnlyList<ParsedFirewallRule> rules, ParsedFirewallRule rule) => GetCompanion(rules, rule) is not null;

    private static ParsedFirewallRule? GetCompanion(IReadOnlyList<ParsedFirewallRule> rules, ParsedFirewallRule rule)
    {
        // UFW emits the IPv4 member immediately before the IPv6 member. Limiting
        // the pairing to adjacent entries avoids merging two intentionally
        // duplicated rules that happen to have identical fields.
        var companionNumber = rule.IsIpv6 ? rule.Rule.Number - 1 : rule.Rule.Number + 1;
        return rules.FirstOrDefault(candidate => candidate.Rule.Number == companionNumber
            && candidate.IsIpv6 != rule.IsIpv6
            && SameRule(candidate.Rule, rule.Rule));
    }

    private static bool SameRule(FirewallRuleDto left, FirewallRuleDto right) =>
        string.Equals(left.Action, right.Action, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Direction, right.Direction, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Protocol, right.Protocol, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Source, right.Source, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Destination, right.Destination, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Port, right.Port, StringComparison.OrdinalIgnoreCase);

    private async Task<CommandResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken)
    {
        var result = await _transport.ExecuteAsync(request, cancellationToken);
        var output = result.OutputBase64 is null ? string.Empty : System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(result.OutputBase64));
        return new(result.Success, output, ToProblemCode(result.ProblemCode));
    }

    private async Task<FirewallOperationResult> ExecuteOperationAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken)
    {
        var result = await _transport.ExecuteAsync(request, cancellationToken);
        if (!result.Success) _logger.LogWarning("Structured Firewall Helper operation failed. Operation={Operation}, Problem={Problem}", request.Operation, result.ProblemCode);
        return new(result.Success, ToProblemCode(result.ProblemCode));
    }

    private static string ToProblemCode(PrivilegedProblemCode code) => code switch
    {
        PrivilegedProblemCode.None => string.Empty,
        PrivilegedProblemCode.UnsupportedOperation => "firewall.ufw_not_installed",
        PrivilegedProblemCode.HelperUnavailable or PrivilegedProblemCode.AccessDenied => "firewall.privileged_proxy_required",
        PrivilegedProblemCode.InvalidRequest => "firewall.invalid_rule",
        _ => "firewall.operation_failed",
    };

    private static FirewallDefaultPolicy ToDefaultPolicy(string value) => value.Trim().ToLowerInvariant() switch
    {
        "allow" => FirewallDefaultPolicy.Allow,
        "deny" => FirewallDefaultPolicy.Deny,
        _ => FirewallDefaultPolicy.Reject,
    };

    private static PrivilegedOperationRequest ToRuleRequest(PrivilegedOperationKind operation, IReadOnlyList<string> args, RuleNumbersResult? numbers = null) => new(operation,
        FirewallRuleAction: Enum.Parse<FirewallRuleAction>(args[0], true),
        FirewallRuleDirection: Enum.Parse<FirewallRuleDirection>(args[1], true),
        FirewallRuleProtocol: Enum.Parse<FirewallRuleProtocol>(args[2], true),
        FirewallSource: args[3], FirewallDestination: args[4], FirewallPort: args[5],
        FirewallRuleNumber: numbers is null ? null : int.Parse(numbers.Numbers[0], System.Globalization.CultureInfo.InvariantCulture),
        FirewallCompanionRuleNumber: numbers?.Numbers[1] == "none" ? null : int.Parse(numbers!.Numbers[1], System.Globalization.CultureInfo.InvariantCulture));

    private static ParsedTarget ParseTarget(string target)
    {
        // `ufw status numbered` has a column-like target field. It is usually
        // `22/tcp`, but can also be `192.0.2.10 22/tcp` or `22/tcp (v6)`.
        // Keeping the destination separate makes the table accurately reflect
        // rules that constrain the local address as well as the port.
        var normalized = target.Trim();
        var isIpv6 = normalized.EndsWith(" (v6)", StringComparison.OrdinalIgnoreCase);
        if (isIpv6) normalized = normalized[..^5].TrimEnd();
        if (normalized.Equals("anywhere", StringComparison.OrdinalIgnoreCase)) return new("any", "any", "any", isIpv6);
        if (TryNormalizeEndpoint(normalized, out var endpoint)) return new(endpoint, "any", "any", isIpv6);

        var parts = normalized.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var portAndProtocol = parts[^1];
        var slash = portAndProtocol.IndexOf('/');
        var port = slash > 0 ? portAndProtocol[..slash] : portAndProtocol;
        var protocol = slash > 0 ? portAndProtocol[(slash + 1)..].ToLowerInvariant() : "any";
        var destination = parts.Length > 1 ? string.Join(' ', parts[..^1]) : "any";
        return new(destination.Equals("anywhere", StringComparison.OrdinalIgnoreCase) ? "any" : destination, port, protocol, isIpv6);
    }

    private static string NormalizeStatusEndpoint(string source, out bool isIpv6)
    {
        var normalized = source.Trim();
        isIpv6 = normalized.EndsWith(" (v6)", StringComparison.OrdinalIgnoreCase);
        if (isIpv6) normalized = normalized[..^5].TrimEnd();
        return normalized.Equals("anywhere", StringComparison.OrdinalIgnoreCase) ? "any" : normalized;
    }

    private static bool IsPolicy(string? value) => value?.Trim().ToLowerInvariant() is "allow" or "deny" or "reject";
    private static bool IsAction(string? value) => value?.Trim().ToLowerInvariant() is "allow" or "deny" or "reject" or "limit";
    private static bool IsDirection(string? value) => value?.Trim().ToLowerInvariant() is "in" or "out";
    private static bool IsProtocol(string? value) => value?.Trim().ToLowerInvariant() is "tcp" or "udp" or "any";

    private static bool TryValidateRule(string? action, string? direction, string? protocol, string? sourceValue, string? destinationValue, string? portValue,
        out IReadOnlyList<string> args, out string problem)
    {
        args = []; problem = "firewall.invalid_rule";
        if (!IsAction(action) || !IsDirection(direction) || !IsProtocol(protocol)) return false;
        if (!TryNormalizeEndpoint(sourceValue, out var source) || !TryNormalizeEndpoint(destinationValue, out var destination)) return false;
        if (!TryNormalizePort(portValue, out var port)) return false;
        args = [action!.Trim().ToLowerInvariant(), direction!.Trim().ToLowerInvariant(),
            protocol!.Trim().ToLowerInvariant(), source, destination, port];
        problem = string.Empty;
        return true;
    }

    private static bool TryNormalizeEndpoint(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? "";
        if (normalized is "any" or "anywhere") { normalized = "any"; return true; }
        var slash = normalized.IndexOf('/');
        var address = slash < 0 ? normalized : normalized[..slash];
        if (!IPAddress.TryParse(address, out _)) return false;
        if (slash < 0) return true;
        return int.TryParse(normalized[(slash + 1)..], out var prefix) && prefix >= 0 && prefix <= (address.Contains(':') ? 128 : 32);
    }

    private static bool TryNormalizePort(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? "";
        if (normalized is "any" or "") { normalized = "any"; return true; }
        var parts = normalized.Split(':');
        return parts.Length is 1 or 2 && parts.All(part => int.TryParse(part, out var port) && port is > 0 and <= 65535)
            && (parts.Length == 1 || int.Parse(parts[0]) <= int.Parse(parts[1]));
    }

    private sealed record CommandResult(bool Success, string Output, string ProblemCode);
    private sealed record ParsedRuleList(IReadOnlyList<ParsedFirewallRule> Rules, string ProblemCode)
    {
        public bool Success => string.IsNullOrEmpty(ProblemCode);
    }
    private sealed record ParsedFirewallRule(FirewallRuleDto Rule, bool IsIpv6);
    private sealed record ParsedTarget(string Destination, string Port, string Protocol, bool IsIpv6);
    private sealed record RuleNumbersResult(IReadOnlyList<string> Numbers, string ProblemCode)
    {
        public bool Success => string.IsNullOrEmpty(ProblemCode);
    }
}
