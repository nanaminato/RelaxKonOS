using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Client.Apps.Docker;

public interface IRemoteDockerClient
{
    Task<DockerStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerContainerDto>> ListContainersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerImageDto>> ListImagesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerNetworkDto>> ListNetworksAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerVolumeDto>> ListVolumesAsync(CancellationToken cancellationToken = default);
    Task<DockerContainerDetailsDto?> GetContainerAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerStackDto>> ListStacksAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerStackServiceDto>> ListStackServicesAsync(string name, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> ApplyContainerActionAsync(string id, string action, DockerContainerActionRequest request, CancellationToken cancellationToken = default);
    Task<DockerStackOperationResult> ApplyStackOperationAsync(string operation, DockerStackDefinitionDto definition, CancellationToken cancellationToken = default);
    Task<DockerStackOperationResult> ApplyStackActionAsync(string name, string action, DockerStackActionRequest request, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> PullImageAsync(DockerImageOperationRequest request, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> DeleteImageAsync(string id, DockerImageOperationRequest request, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> CreateContainerAsync(DockerContainerCreateRequest request, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> UpdateContainerAsync(string id, DockerContainerUpdateRequest request, CancellationToken cancellationToken = default);
    Task<DockerStackDefinitionDto?> GetStackDefinitionAsync(string name, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> CreateNetworkAsync(DockerNetworkCreateRequest request, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> CreateVolumeAsync(DockerVolumeCreateRequest request, CancellationToken cancellationToken = default);
    Task<DockerNetworkDetailsDto?> GetNetworkAsync(string id, CancellationToken cancellationToken = default);
    Task<DockerVolumeDetailsDto?> GetVolumeAsync(string name, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> DeleteNetworkAsync(string id, bool confirmed, CancellationToken cancellationToken = default);
    Task<DockerOperationResult> DeleteVolumeAsync(string name, bool confirmed, CancellationToken cancellationToken = default);
    Task<DockerContainerLogsDto?> GetContainerLogsAsync(string id, int tail = 200, CancellationToken cancellationToken = default);
    Task<DockerContainerStatsDto?> GetContainerStatsAsync(string id, CancellationToken cancellationToken = default);
    Task<DockerProxyStatusDto> GetProxyStatusAsync(CancellationToken cancellationToken = default);
    Task<DockerProxyStatusDto> SaveProxyAsync(SaveDockerProxySettingsRequest request, CancellationToken cancellationToken = default);
    Task<DockerProxyStatusDto> ClearProxyAsync(CancellationToken cancellationToken = default);
    /// <summary>Starts, stops, or restarts the machine's whole Docker engine.</summary>
    Task<DockerEngineControlResult> ApplyEngineActionAsync(DockerEngineAction action, bool confirmed, CancellationToken cancellationToken = default);
}

/// <summary>
/// A proxy write the server refused, carrying its stable problem code. The server rejects a
/// preference with <c>400</c> and leaves the previous one untouched, so the caller needs the code
/// to explain what was wrong instead of showing a transport error.
/// </summary>
public sealed class DockerProxyRequestException(string problemCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
}
