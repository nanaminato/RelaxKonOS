namespace RelaxKonOS.Protocol.Docker;

/// <summary>Compose input kept structured at the API boundary; it is never treated as shell input.</summary>
public sealed record DockerStackDefinitionDto(string Name, string ComposeYaml);
/// <summary>Confirmation is required before taking down a Compose project.</summary>
public sealed record DockerStackActionRequest(bool Confirmed = false);

/// <summary>
/// Compose project reported by the local Docker CLI.  The source location is deliberately
/// included so an operator can audit and open the Compose file in RemoteExplorer.
/// </summary>
public sealed record DockerStackDto(string Name, string Status, string ConfigFiles, string ConfigDirectory);

/// <summary>A Compose-managed container, grouped by its Compose service.</summary>
public sealed record DockerStackServiceDto(string Service, string Container, string Image, string State, string Status);

/// <summary>Bounded diagnostic result for a Compose operation.</summary>
public sealed record DockerStackOperationResult(bool Success, string ProblemCode, IReadOnlyList<string> Messages);
