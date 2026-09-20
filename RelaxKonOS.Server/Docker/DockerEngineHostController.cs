using RelaxKonOS.Protocol.Docker;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Docker;

/// <summary>Host-level outcome of an engine lifecycle command. The problem code is a localization key.</summary>
public sealed record DockerEngineHostCommandResult(bool Success, string ProblemCode);

/// <summary>
/// Owns the platform mechanism for starting, stopping, and restarting the Docker engine. It mirrors
/// <see cref="IDockerEngineProxyConfigurator"/>: a native Linux daemon is a systemd unit that only
/// the privileged Helper may drive, while Docker Desktop owns its own daemon and exposes lifecycle
/// through its own CLI. Endpoints and the client therefore never branch on the operating system.
/// </summary>
public interface IDockerEngineHostController
{
    /// <summary>Stable platform identifier shown to the operator, e.g. <c>linux-systemd</c>.</summary>
    string Platform { get; }
    /// <summary>Whether this host has a mechanism for controlling the engine at all.</summary>
    bool IsSupported { get; }
    /// <summary>Runs the command. The caller owns confirmation, because it kills running containers.</summary>
    Task<DockerEngineHostCommandResult> ApplyAsync(DockerEngineAction action, CancellationToken cancellationToken = default);
}

public sealed class DockerEngineHostController(IPrivilegedOperationTransport transport, ILogger<DockerEngineHostController> logger)
    : IDockerEngineHostController
{
    public string Platform => OperatingSystem.IsLinux() ? "linux-systemd" : OperatingSystem.IsWindows() ? "docker-desktop-windows" : "unsupported";

    public bool IsSupported => OperatingSystem.IsLinux() || OperatingSystem.IsWindows();

    public async Task<DockerEngineHostCommandResult> ApplyAsync(DockerEngineAction action, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsLinux()) return await ApplyLinuxAsync(action, cancellationToken);
        if (OperatingSystem.IsWindows()) return await ApplyDesktopAsync(action, cancellationToken);
        return new(false, DockerEngineProblem.PlatformUnsupported);
    }

    /// <summary>
    /// The unit name is a Helper constant, so the request carries only the action. The Helper does
    /// not enable or disable the unit, which leaves the host's boot policy untouched.
    /// </summary>
    private async Task<DockerEngineHostCommandResult> ApplyLinuxAsync(DockerEngineAction action, CancellationToken cancellationToken)
    {
        var requested = action switch
        {
            DockerEngineAction.Start => PrivilegedServiceAction.Start,
            DockerEngineAction.Stop => PrivilegedServiceAction.Stop,
            _ => PrivilegedServiceAction.Restart,
        };
        var result = await transport.ExecuteAsync(
            new PrivilegedOperationRequest(PrivilegedOperationKind.DockerEngineServiceAction, DockerServiceAction: requested),
            cancellationToken);
        if (result.Success) return new(true, string.Empty);
        logger.LogWarning("Docker engine service action failed. ProblemCode={ProblemCode}", result.ProblemCode);
        return new(false, result.ProblemCode switch
        {
            PrivilegedProblemCode.UnsupportedOperation => DockerEngineProblem.PlatformUnsupported,
            PrivilegedProblemCode.HelperUnavailable => DockerEngineProblem.HelperUnavailable,
            PrivilegedProblemCode.AccessDenied => DockerEngineProblem.HelperUnavailable,
            _ => DockerEngineProblem.ActionFailed,
        });
    }

    /// <summary>
    /// Docker Desktop's plugin accepts exactly the three lifecycle words this feature exposes, so a
    /// segment is passed through rather than translated into a second vocabulary.
    /// </summary>
    private static async Task<DockerEngineHostCommandResult> ApplyDesktopAsync(DockerEngineAction action, CancellationToken cancellationToken)
    {
        var timeout = action == DockerEngineAction.Stop ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(3);
        return await DockerDesktopCli.RunAsync(DockerEngineActionRoutes.Segment(action), timeout, cancellationToken)
            ? new(true, string.Empty)
            : new(false, DockerEngineProblem.DesktopCommandFailed);
    }
}
