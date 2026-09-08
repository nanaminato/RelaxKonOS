namespace RelaxKonOS.Protocol.Docker;

/// <summary>Safe, non-secret status of the server's local Docker Engine.</summary>
public sealed record DockerStatusDto(
    bool IsAvailable,
    string ProblemCode,
    string? ServerVersion,
    string? OperatingSystem,
    string? Architecture);
