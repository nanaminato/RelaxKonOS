namespace RelaxKonOS.Server.ProcessGuardian;

/// <summary>A failed Agent read must never be projected as an authoritative empty collection.</summary>
public sealed class GuardianReadException(string problemCode) : Exception("Guardian read failed.")
{
    public string ProblemCode { get; } = problemCode;
}
