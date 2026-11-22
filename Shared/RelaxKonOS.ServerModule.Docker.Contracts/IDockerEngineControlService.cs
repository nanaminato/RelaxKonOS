using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Docker;

public static class DockerEngineProblem
{
    public const string InvalidAction = "docker.engine.problem.invalid_action";
    public const string ConfirmationRequired = "docker.engine.problem.confirmation_required";
    public const string PlatformUnsupported = "docker.engine.problem.platform_unsupported";
    public const string HelperUnavailable = "docker.engine.problem.helper_unavailable";
    public const string DesktopCommandFailed = "docker.engine.problem.desktop_command_failed";
    public const string ActionFailed = "docker.engine.problem.action_failed";
}

public interface IDockerEngineControlService
{
    Task<DockerEngineControlResult> ApplyAsync(string action, bool confirmed, CancellationToken cancellationToken = default);
}

public interface IDockerEngineControlHost
{
    bool IsSupported { get; }
    Task<DockerEngineControlResult> ApplyAsync(DockerEngineAction action, CancellationToken cancellationToken);
}
