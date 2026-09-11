using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Docker;

/// <summary>Produces host-action plans only; it must never self-elevate or request host passwords.</summary>
public interface IDockerRuntimeInstaller
{
    Task<DockerInstallationPlanDto> CreatePlanAsync(CancellationToken cancellationToken = default);
    Task<DockerOperationResult> InstallAsync( CancellationToken cancellationToken = default);
}
