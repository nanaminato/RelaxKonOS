using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Docker;

public interface IDockerStackOutcomePolicy
{
    (DockerStackOperationState State, string? Problem, string? Recovery) Classify(
        DockerStackOperationKind kind, DockerStackMutationResult result, IReadOnlyList<string> desired,
        IReadOnlyList<DockerStackServiceDto> observed);

    string? RecoveryCode(DockerStackOperationKind kind, IReadOnlyList<DockerStackServiceDto> observed);
}
