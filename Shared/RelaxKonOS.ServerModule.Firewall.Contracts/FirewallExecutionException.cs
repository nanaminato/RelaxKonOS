namespace RelaxKonOS.Server.Firewall;

/// <summary>Sanitized execution failure reconstructed by Host; never carries a private inner stack.</summary>
public sealed class FirewallExecutionException(string problemCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
}
