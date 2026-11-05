namespace RelaxKonOS.Server.Firewall;

/// <summary>A failed host read cannot be represented as an authoritative empty rule set.</summary>
public sealed class FirewallRulesUnavailableException(string problemCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
}
