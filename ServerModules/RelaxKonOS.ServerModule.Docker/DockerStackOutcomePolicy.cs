using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Docker;

public sealed class DockerStackOutcomePolicy : IDockerStackOutcomePolicy
{
    /// <summary>
    /// Turns "the command ran" plus "here is what the Engine actually runs" into a terminal outcome.
    /// The desired set comes from the Compose parser for a deployment and from the observed set itself
    /// for a lifecycle action, so a service that never appeared still counts as missing.
    /// </summary>
    public (DockerStackOperationState State, string? Problem, string? Recovery) Classify(
        DockerStackOperationKind kind, DockerStackMutationResult result, IReadOnlyList<string> desired, IReadOnlyList<DockerStackServiceDto> observed)
    {
        if (kind == DockerStackOperationKind.Delete)
        {
            if (observed.Count > 0) return (DockerStackOperationState.PartialFailed, DockerStackProblem.PartialFailure, null);
            return result.Success
                ? (DockerStackOperationState.Succeeded, null, null)
                : (DockerStackOperationState.Failed, result.ProblemCode, null);
        }

        if (!result.Success)
        {
            return observed.Count == 0
                ? (DockerStackOperationState.Failed, result.ProblemCode, null)
                : (DockerStackOperationState.PartialFailed, result.ProblemCode, DockerStackProblem.PartialFailure);
        }

        var services = desired.Count > 0 ? desired : [.. observed.Select(service => service.Service)];
        var unsatisfied = kind == DockerStackOperationKind.Stop
            ? services.Where(service => observed.Any(item => item.Service == service && IsRunning(item.State))).ToArray()
            : services.Where(service => !observed.Any(item => item.Service == service && IsRunning(item.State))).ToArray();
        // The command succeeded, so the partial failure is the whole verdict and there is no second
        // code to add: a recovery code that merely repeats the primary one tells a caller nothing.
        return unsatisfied.Length == 0
            ? (DockerStackOperationState.Succeeded, null, null)
            : (DockerStackOperationState.PartialFailed, DockerStackProblem.PartialFailure, null);
    }

    private static bool IsRunning(string state) => state.Equals("running", StringComparison.OrdinalIgnoreCase);

    /// <summary>Which follow-up the operator most likely needs after an unverified operation. A project
    /// left holding containers after a delete, or a deployment that left nothing running, are the two
    /// cases where doing nothing is not an option.</summary>
    public string? RecoveryCode(DockerStackOperationKind kind, IReadOnlyList<DockerStackServiceDto> observed)
    {
        if (observed.Count == 0) return kind is DockerStackOperationKind.Deploy or DockerStackOperationKind.Start or DockerStackOperationKind.Restart
            ? DockerStackProblem.PartialFailure
            : null;
        return kind == DockerStackOperationKind.Delete ? DockerStackProblem.PartialFailure : null;
    }
}
