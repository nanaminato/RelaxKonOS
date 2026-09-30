namespace RelaxKonOS.Server.FileServices;

/// <summary>Failed or malformed Helper reads cannot establish an empty managed configuration.</summary>
public sealed class FileServiceReadException(string problemCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
}
