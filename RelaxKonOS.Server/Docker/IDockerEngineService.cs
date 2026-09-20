using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Docker;

/// <summary>
/// Proxy values the Docker daemon reports for itself. This is the only trustworthy read-back:
/// Docker Desktop ignores proxies configured in <c>daemon.json</c>, so the saved preference alone
/// proves nothing about what the daemon actually uses.
/// </summary>
public sealed record DockerEngineProxyState(string HttpProxy, string HttpsProxy, string NoProxy);

/// <summary>The only server boundary allowed to invoke the host's local Docker CLI/transport.</summary>
public interface IDockerEngineService
{
    Task<DockerStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns null when the daemon is unreachable or does not report proxy information.</summary>
    Task<DockerEngineProxyState?> GetProxyStateAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerContainerDto>> ListContainersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerImageDto>> ListImagesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerNetworkDto>> ListNetworksAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerVolumeDto>> ListVolumesAsync(CancellationToken cancellationToken = default);
    Task<DockerContainerDetailsDto?> GetContainerAsync(string id, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> ApplyContainerActionAsync(string containerId, string action, DockerContainerActionRequest request, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> PullImageAsync(DockerImageOperationRequest request, string? resolvedImageReference = null, CancellationToken cancellationToken = default, Action<string>? onOutput = null);
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
    /// <param name="includeBuildOutput">Returns the build's own stdout/stderr to the caller. It defaults
    /// to false because build output can echo Dockerfile content and build arguments, so only a caller
    /// that owns the context being built may ask for it.</param>
    Task<DockerOperationResult> BuildImageAsync(DockerBuildRequest request, bool includeBuildOutput = false, CancellationToken cancellationToken = default, Action<string>? onOutput = null);
    Task<DockerImageArchiveDto?> ExportImageAsync(string imageId, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> ImportImageAsync(DockerImageArchiveDto archive, CancellationToken cancellationToken = default);
}
