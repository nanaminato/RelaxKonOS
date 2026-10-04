namespace RelaxKonOS.Protocol.Firewall;

/// <summary>Read-only host firewall status. The API intentionally exposes rules structurally, never command text.</summary>
public sealed record FirewallStatusDto(
    bool IsAvailable,
    bool IsEnabled,
    string Backend,
    string? Version,
    string? DefaultIncomingPolicy,
    string? DefaultOutgoingPolicy,
    string ProblemCode = "");

public sealed record FirewallRuleDto(
    int Number,
    string Action,
    string Direction,
    string Protocol,
    string Source,
    string Destination,
    string Port)
{
    /// <summary>
    /// Address family covered by this logical UFW rule: IPv4, IPv6, or IPv4 + IPv6.
    /// This makes UFW's otherwise identical-looking paired entries distinguishable in clients.
    /// </summary>
    public string AddressFamily { get; init; } = "IPv4";
}

public sealed record CreateFirewallRuleRequest(
    string Action,
    string Direction,
    string Protocol,
    string Source,
    string Destination,
    string Port);

/// <summary>
/// Replaces a managed firewall rule identified by its number. The rule itself remains structured so
/// the API never becomes a pass-through for UFW command text.
/// </summary>
public sealed record UpdateFirewallRuleRequest(
    string Action,
    string Direction,
    string Protocol,
    string Source,
    string Destination,
    string Port);

public sealed record UpdateFirewallEnabledRequest(bool Enabled);

public sealed record UpdateFirewallDefaultsRequest(
    string IncomingPolicy,
    string OutgoingPolicy);

public sealed record FirewallOperationResult(bool Success, string ProblemCode = "");
