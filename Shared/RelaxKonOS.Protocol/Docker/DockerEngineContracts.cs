namespace RelaxKonOS.Protocol.Docker;

/// <summary>
/// Lifecycle actions for the machine's Docker engine. Unlike a container action these are
/// host-wide: stopping or restarting the engine terminates every running container on the host, so
/// the caller has to confirm them explicitly.
/// </summary>
public enum DockerEngineAction
{
    Start,
    Stop,
    Restart,
}

/// <summary>
/// The wire representation of <see cref="DockerEngineAction"/>. Both the route segment and the
/// privileged mapping are derived from one table, so the client and the server cannot drift into
/// two spellings of the same action.
/// </summary>
public static class DockerEngineActionRoutes
{
    public static string Segment(DockerEngineAction action) => action switch
    {
        DockerEngineAction.Start => "start",
        DockerEngineAction.Stop => "stop",
        DockerEngineAction.Restart => "restart",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>Resolves a route segment. An unknown segment is rejected rather than guessed at.</summary>
    public static bool TryParse(string? segment, out DockerEngineAction action)
    {
        switch (segment)
        {
            case "start": action = DockerEngineAction.Start; return true;
            case "stop": action = DockerEngineAction.Stop; return true;
            case "restart": action = DockerEngineAction.Restart; return true;
            default: action = default; return false;
        }
    }
}

/// <summary>
/// Request for an engine lifecycle action. <see cref="Confirmed"/> is mandatory for
/// <see cref="DockerEngineAction.Stop"/> and <see cref="DockerEngineAction.Restart"/>, because
/// those interrupt every running container on the machine.
/// </summary>
public sealed record DockerEngineActionRequest(bool Confirmed = false);

/// <summary>
/// Outcome of an engine lifecycle action together with the engine state read afterwards, so a
/// caller learns whether the engine actually came back without issuing a second request.
/// <see cref="Status"/> is null when the action was rejected before it reached the host.
/// </summary>
public sealed record DockerEngineControlResult(bool Success, string ProblemCode, DockerStatusDto? Status);
