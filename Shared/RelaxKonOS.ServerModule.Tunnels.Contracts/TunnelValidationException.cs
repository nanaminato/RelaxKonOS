namespace RelaxKonOS.ServerModule.Tunnels;

public sealed class TunnelValidationException(string problemCode) : Exception(problemCode) { public string ProblemCode { get; } = problemCode; }
