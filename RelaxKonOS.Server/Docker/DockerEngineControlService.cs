using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Docker;

/// <summary>
/// Stable problem codes for engine lifecycle control. Each value is also the localization key the
/// client resolves, so a new code needs a matching string in the Docker Manager resources.
/// </summary>
public static class DockerEngineProblem
{
    /// <summary>The route segment is not one of the supported lifecycle actions.</summary>
    public const string InvalidAction = "docker.engine.problem.invalid_action";
    /// <summary>Stopping or restarting kills every running container, so it needs confirmation.</summary>
    public const string ConfirmationRequired = "docker.engine.problem.confirmation_required";
    /// <summary>This host has no supported mechanism for controlling the engine.</summary>
    public const string PlatformUnsupported = "docker.engine.problem.platform_unsupported";
    /// <summary>The privileged helper that owns the daemon unit is unavailable.</summary>
    public const string HelperUnavailable = "docker.engine.problem.helper_unavailable";
    /// <summary>The Docker Desktop CLI rejected the lifecycle command or timed out.</summary>
    public const string DesktopCommandFailed = "docker.engine.problem.desktop_command_failed";
    /// <summary>The host command failed for a reason that has no more specific code.</summary>
    public const string ActionFailed = "docker.engine.problem.action_failed";
}

public interface IDockerEngineControlService
{
    /// <param name="action">Route segment, e.g. <c>restart</c>.</param>
    /// <param name="confirmed">True when the operator accepted that running containers will stop.</param>
    Task<DockerEngineControlResult> ApplyAsync(string action, bool confirmed, CancellationToken cancellationToken = default);
}

/// <summary>
/// Host-wide control of the machine's Docker engine. The engine is a machine resource rather than a
/// per-container one, so this is not part of the container action surface even though both end up
/// running the same CLI on Linux.
/// </summary>
public sealed class DockerEngineControlService(
    IDockerEngineHostController controller,
    IDockerEngineService engine,
    ILogger<DockerEngineControlService> logger) : IDockerEngineControlService
{
    public async Task<DockerEngineControlResult> ApplyAsync(string action, bool confirmed, CancellationToken cancellationToken = default)
    {
        if (!DockerEngineActionRoutes.TryParse(action, out var parsed))
            return new(false, DockerEngineProblem.InvalidAction, null);
        if (!controller.IsSupported)
            return new(false, DockerEngineProblem.PlatformUnsupported, null);
        // Stop and restart terminate every running container on the host, so neither happens as the
        // side effect of a single request.
        if (parsed != DockerEngineAction.Start && !confirmed)
            return new(false, DockerEngineProblem.ConfirmationRequired, null);

        logger.LogInformation("Docker engine action requested. Action={Action}", DockerEngineActionRoutes.Segment(parsed));
        var result = await controller.ApplyAsync(parsed, cancellationToken);

        // The status is read after the action so the caller learns whether the engine came back
        // instead of having to poll for it. A stop is expected to leave the daemon unreachable, so
        // that is reported through the status rather than treated as a failure of the action.
        var status = await ReadStatusAsync(parsed, cancellationToken);
        return result.Success ? new(true, string.Empty, status) : new(false, result.ProblemCode, status);
    }

    private async Task<DockerStatusDto?> ReadStatusAsync(DockerEngineAction action, CancellationToken cancellationToken)
    {
        try { return await engine.GetStatusAsync(cancellationToken); }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            logger.LogWarning("The Docker engine status could not be read after an engine action. Action={Action}",
                DockerEngineActionRoutes.Segment(action));
            return null;
        }
    }
}
