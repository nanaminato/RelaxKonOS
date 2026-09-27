namespace RelaxKonOS.Protocol.Docker;

/// <summary>
/// One Compose project change submitted as a durable operation. A stack operation is deliberately not
/// a synchronous request: it survives the HTTP call that started it, survives the phone that asked
/// for it, and is reconciled against the real Engine after a server restart.
/// </summary>
public enum DockerStackOperationKind
{
    Deploy,
    Start,
    Stop,
    Restart,
    Delete,
}

/// <summary>
/// Lifetime of a durable stack operation.
/// <list type="bullet">
/// <item><see cref="PartialFailed"/>: some services of the project are not in their desired state.
/// The operation is terminal, every observed service is reported, and the caller chooses a recovery
/// action. The server never claims a whole-project rollback it did not perform.</item>
/// <item><see cref="Interrupted"/>: the process stopped while the operation was active. Reconciliation
/// observed the project but cannot prove the original intent completed, so the outcome stays
/// unverified instead of being reported as success.</item>
/// </list>
/// </summary>
public enum DockerStackOperationState
{
    Queued,
    Running,
    Succeeded,
    PartialFailed,
    Failed,
    Cancelled,
    Interrupted,
}

/// <summary>Where an active operation currently is. A terminal state reports its own matching stage.</summary>
public enum DockerStackOperationStage
{
    Queued,
    Preflight,
    Parsing,
    Applying,
    Observing,
    Completed,
    Failed,
    Cancelled,
    Interrupted,
}

/// <summary>
/// The wire spelling of a stack action. The route segment and the operation kind share one table so a
/// client cannot invent a fifth lifecycle verb.
/// </summary>
public static class DockerStackActionRoutes
{
    public static string Segment(DockerStackOperationKind kind) => kind switch
    {
        DockerStackOperationKind.Deploy => "deploy",
        DockerStackOperationKind.Start => "start",
        DockerStackOperationKind.Stop => "stop",
        DockerStackOperationKind.Restart => "restart",
        DockerStackOperationKind.Delete => "delete",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Resolves an action segment. Only the two whole-project lifecycle verbs are actions;
    /// a deployment is submitted to its own route because it also carries a definition.</summary>
    public static bool TryParseAction(string? segment, out DockerStackOperationKind kind)
    {
        switch (segment)
        {
            case "start": kind = DockerStackOperationKind.Start; return true;
            case "stop": kind = DockerStackOperationKind.Stop; return true;
            case "restart": kind = DockerStackOperationKind.Restart; return true;
            case "delete": kind = DockerStackOperationKind.Delete; return true;
            default: kind = default; return false;
        }
    }
}

/// <summary>
/// A durable Compose operation. The record here — not a live event — is the authoritative answer to
/// "what happened to my stack", so a client that lost its connection can still read it.
/// </summary>
/// <param name="Services">Services observed on the Engine when the operation last looked. For an active
/// operation this is a snapshot, and for a terminal one it is the outcome. It is never inferred from
/// the Compose source.</param>
/// <param name="RecoveryProblemCode">Set only when the operation left the project in a state that needs
/// the operator to choose between retrying, stopping, or removing it, <em>and</em> the primary code does
/// not already say so. It therefore never repeats <paramref name="ProblemCode"/>; two identical codes
/// would tell a caller nothing about which follow-up is expected.</param>
public sealed record DockerStackOperationDto(
    Guid OperationId,
    string ProjectName,
    DockerStackOperationKind Kind,
    DockerStackOperationState State,
    DockerStackOperationStage Stage,
    string? ProblemCode,
    string? RecoveryProblemCode,
    string RequestedByReference,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    bool Cancellable,
    IReadOnlyList<DockerStackServiceDto> Services);

/// <summary>
/// The result of parsing a definition without applying it. It exists so the operator approves the same
/// input that will be executed: <see cref="DefinitionVersion"/> travels back with the deployment and is
/// rejected if the document changed in between.
/// </summary>
/// <param name="DefinitionVersion">Content identity of the submitted source (name plus YAML). It is not
/// a security boundary; it is the guard that stops a preview from being approved for a different file.</param>
/// <param name="Services">Services the Compose parser resolved, with the image each one will run.</param>
public sealed record DockerStackPreviewDto(
    string ProjectName,
    string DefinitionVersion,
    IReadOnlyList<DockerStackPreviewServiceDto> Services,
    IReadOnlyList<string> Volumes,
    IReadOnlyList<string> Networks);

/// <summary>One service of a parsed definition. Ports are the container-side declarations, which is what
/// the phone can verify without a second, disagreeing Compose implementation.</summary>
public sealed record DockerStackPreviewServiceDto(string Service, string Image, IReadOnlyList<string> Ports);

/// <summary>A deployment bound to the preview the operator approved.</summary>
public sealed record DockerStackDeployRequest(DockerStackDefinitionDto Definition, string DefinitionVersion);

/// <summary>Bounded, sanitized output of the step that produced a stack operation outcome.</summary>
/// <param name="Truncated">True when the head of <paramref name="Lines"/> was dropped, so a reader never
/// mistakes a tail for the complete log.</param>
public sealed record DockerStackOperationDiagnosticsDto(IReadOnlyList<string> Lines, bool Truncated);
