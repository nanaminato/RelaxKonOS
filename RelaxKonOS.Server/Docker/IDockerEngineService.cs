using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Docker;

/// <summary>The only server boundary allowed to invoke the host's local Docker CLI/transport.</summary>
public interface IDockerEngineService
{
    Task<DockerStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerContainerDto>> ListContainersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerImageDto>> ListImagesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerNetworkDto>> ListNetworksAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerVolumeDto>> ListVolumesAsync(CancellationToken cancellationToken = default);
    Task<DockerContainerDetailsDto?> GetContainerAsync(string id, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> ApplyContainerActionAsync(string containerId, string action, DockerContainerActionRequest request, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> PullImageAsync(DockerImageOperationRequest request, string? resolvedImageReference = null, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> DeleteImageAsync(string imageId, DockerImageOperationRequest request, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> CreateContainerAsync(DockerContainerCreateRequest request, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> UpdateContainerAsync(string id, DockerContainerUpdateRequest request, CancellationToken cancellationToken = default);
    Task<DockerNetworkDetailsDto?> GetNetworkAsync(string id, CancellationToken cancellationToken = default);
    Task<DockerVolumeDetailsDto?> GetVolumeAsync(string name, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> CreateNetworkAsync(DockerNetworkCreateRequest request, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> CreateVolumeAsync(DockerVolumeCreateRequest request, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> DeleteNetworkAsync(string id, bool confirmed, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> DeleteVolumeAsync(string name, bool confirmed, CancellationToken cancellationToken = default);
    Task<DockerContainerLogsDto?> GetContainerLogsAsync(string id, int tail, CancellationToken cancellationToken = default);
    Task<DockerContainerStatsDto?> GetContainerStatsAsync(string id, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> BuildImageAsync(DockerBuildRequest request, CancellationToken cancellationToken = default);
    Task<DockerImageArchiveDto?> ExportImageAsync(string imageId, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> ImportImageAsync(DockerImageArchiveDto archive, CancellationToken cancellationToken = default);
}
