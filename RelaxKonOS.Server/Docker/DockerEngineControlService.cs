using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Docker;

/// <summary>Routes Engine lifecycle requests through the selected module generation.</summary>
public sealed class DockerEngineControlService(
    IDockerEngineHostController controller,
    IDockerEngineService engine,
    ILogger<DockerEngineControlService> logger) : IDockerEngineControlService
{
    internal DockerComposeExecution Execution { private get; init; } = DockerComposeExecution.Shared;

    public async Task<DockerEngineControlResult> ApplyAsync(string action, bool confirmed, CancellationToken cancellationToken = default)
    {
        using var lease = Execution.Acquire<IDockerEngineControlService>("RelaxKonOS.Server.Docker.DockerEngineControlExecution",
            new Host(controller, engine, logger));
        return await lease.Executor.ApplyAsync(action, confirmed, cancellationToken).ConfigureAwait(false);
    }

    private sealed class Host(IDockerEngineHostController controller, IDockerEngineService engine,
        ILogger<DockerEngineControlService> logger) : IDockerEngineControlHost
    {
        public bool IsSupported => controller.IsSupported;

        public async Task<DockerEngineControlResult> ApplyAsync(DockerEngineAction action, CancellationToken cancellationToken)
        {
            logger.LogInformation("Docker engine action requested. Action={Action}", DockerEngineActionRoutes.Segment(action));
            var result = await controller.ApplyAsync(action, cancellationToken);
            DockerStatusDto? status = null;
            try { status = await engine.GetStatusAsync(cancellationToken); }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            {
                logger.LogWarning("The Docker engine status could not be read after an engine action. Action={Action}", DockerEngineActionRoutes.Segment(action));
            }
            return new(result.Success, result.Success ? string.Empty : result.ProblemCode, status);
        }
    }
}
