using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Docker;

/// <summary>Controlled Compose executor. Callers supply structured definitions, never shell command strings.</summary>
public interface IDockerComposeService
{
    Task<IReadOnlyList<DockerStackDto>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Parses a definition without applying it and reports the services it resolves to. This is the only
    /// authority on what a document means; a client never re-implements Compose to predict it.
    /// </summary>
    Task<DockerStackPreviewDto> PreviewAsync(DockerStackDefinitionDto definition, CancellationToken cancellationToken = default);

    Task<DockerStackDefinitionDto?> GetDefinitionAsync(string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerStackServiceDto>> ListServicesAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Persists the source and brings the project to that source. <c>up</c> is Compose's own
    /// update path, so a redeploy of a changed document is the same call as the first deployment.</summary>
    Task<DockerStackMutationResult> DeployAsync(DockerStackDefinitionDto definition, CancellationToken cancellationToken = default);

    /// <summary>Applies a whole-project lifecycle action. <c>delete</c> deliberately retains named volumes.</summary>
    Task<DockerStackMutationResult> ApplyActionAsync(string name, DockerStackOperationKind action, bool confirmed, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of one Compose command. Messages are bounded and are sanitized before they are persisted.</summary>
public sealed record DockerStackMutationResult(bool Success, string ProblemCode, IReadOnlyList<string> Messages);
