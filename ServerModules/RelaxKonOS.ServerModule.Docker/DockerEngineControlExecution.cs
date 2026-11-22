using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Docker;

public sealed class DockerEngineControlExecution(IDockerEngineControlHost host) : IDockerEngineControlService
{
    public Task<DockerEngineControlResult> ApplyAsync(string action, bool confirmed, CancellationToken cancellationToken = default)
    {
        if (!DockerEngineActionRoutes.TryParse(action, out var parsed))
            return Task.FromResult(new DockerEngineControlResult(false, DockerEngineProblem.InvalidAction, null));
        if (!host.IsSupported)
            return Task.FromResult(new DockerEngineControlResult(false, DockerEngineProblem.PlatformUnsupported, null));
        if (parsed != DockerEngineAction.Start && !confirmed)
            return Task.FromResult(new DockerEngineControlResult(false, DockerEngineProblem.ConfirmationRequired, null));

        return host.ApplyAsync(parsed, cancellationToken);
    }
}
