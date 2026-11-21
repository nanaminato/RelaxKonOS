using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.AppSDK;

/// <summary>Read-only Docker engine status and resource inventory for the captured server session.</summary>
public interface IServerDockerInventory
{
    Task<DockerStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerContainerDto>> ListContainersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerImageDto>> ListImagesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerNetworkDto>> ListNetworksAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerVolumeDto>> ListVolumesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerStackDto>> ListStacksAsync(CancellationToken cancellationToken = default);
}
