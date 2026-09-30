namespace RelaxKonOS.Server.Docker;

/// <summary>A failed resource/reference observation must not be presented as an empty collection.</summary>
public sealed class DockerReadException(string problemCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
}
