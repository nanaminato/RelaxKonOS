namespace RelaxKonOS.Protocol.Firewall;

/// <summary>Read-only UFW status. The API intentionally exposes rules structurally, never command text.</summary>
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

/// <summary>One-shot credential confirmation. It is ignored for root and must never be persisted.</summary>
public sealed record FirewallCredentialConfirmation(string Password);

public sealed record CreateFirewallRuleRequest(
    string Action,
    string Direction,
    string Protocol,
    string Source,
    string Destination,
    string Port,
    FirewallCredentialConfirmation? CredentialConfirmation);

/// <summary>
/// Replaces the numbered UFW rule in place. The rule itself remains structured so
/// the API never becomes a pass-through for UFW command text.
/// </summary>
public sealed record UpdateFirewallRuleRequest(
    string Action,
    string Direction,
    string Protocol,
    string Source,
    string Destination,
    string Port,
    FirewallCredentialConfirmation? CredentialConfirmation);

public sealed record UpdateFirewallEnabledRequest(bool Enabled, FirewallCredentialConfirmation? CredentialConfirmation);

public sealed record UpdateFirewallDefaultsRequest(
    string IncomingPolicy,
    string OutgoingPolicy,
    FirewallCredentialConfirmation? CredentialConfirmation);

public sealed record DeleteFirewallRuleRequest(FirewallCredentialConfirmation? CredentialConfirmation);

public sealed record FirewallOperationResult(bool Success, string ProblemCode = "");
