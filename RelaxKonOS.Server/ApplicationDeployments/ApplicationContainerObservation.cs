using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.Docker;
using RelaxKonOS.Server.Docker;

namespace RelaxKonOS.Server.ApplicationDeployments;

/// <summary>Reads the canonical container without creating or changing a workload.</summary>
internal sealed class ApplicationContainerObservation(IDockerEngineService engine)
{
    internal Task<IReadOnlyList<DockerContainerDto>> ListAsync(CancellationToken cancellationToken) => ListAsync(engine, cancellationToken);

    internal static async Task<IReadOnlyList<DockerContainerDto>> ListAsync(IDockerEngineService engine, CancellationToken cancellationToken)
    {
        try { return await engine.ListContainersAsync(cancellationToken); }
        catch (DockerReadException exception)
        {
            throw new ApplicationDeploymentException(exception.ProblemCode == "docker.not_installed"
                ? ApplicationDeploymentProblemCodes.EngineNotInstalled : ApplicationDeploymentProblemCodes.EngineUnavailable, 409);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.EngineUnavailable, 409);
        }
    }

    /// <summary>The observed canonical container plus whether the observation itself succeeded.</summary>
    internal async Task<RuntimeView> ObserveAsync(ApplicationRecord application, CancellationToken cancellationToken)
    {
        IReadOnlyList<DockerContainerDto> containers;
        try { containers = await ListAsync(engine, cancellationToken); }
        catch (ApplicationDeploymentException) { return new(null, false, true); }

        var container = Match(containers, application);
        if (container is null) return new(null, true, true);
        try
        {
            var details = await engine.GetContainerAsync(container.Id, cancellationToken);
            return new(container, true, details is not null && ApplicationDeploymentValidation.IsOwnedBy(details.Labels, application.Id));
        }
        catch (ApplicationDeploymentException) { return new(container, true, true); }
    }

    internal static DockerContainerDto? Match(IReadOnlyList<DockerContainerDto> containers, ApplicationRecord application)
    {
        var name = application.ManagedCompose ? application.ContainerName : ApplicationDeploymentValidation.ContainerName(application.Id);
        return containers.FirstOrDefault(container => container.Names
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(candidate => string.Equals(candidate, name, StringComparison.Ordinal)));
    }

    internal readonly record struct RuntimeView(DockerContainerDto? Container, bool EngineAvailable, bool Owned);
}
