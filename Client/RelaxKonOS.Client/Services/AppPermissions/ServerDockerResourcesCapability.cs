using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Docker;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Docker;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ServerDockerResourcesCapability(AppId appId, IAppPermissionManager permissions,
    UsageMemoryScope connection, IRemoteDockerClient client) : IServerDockerResources
{
    public Task<DockerStatusDto> GetStatusAsync(CancellationToken cancellationToken) =>
        ReadAsync(client.GetStatusAsync, cancellationToken);
    public Task<IReadOnlyList<DockerContainerDto>> ListContainersAsync(CancellationToken cancellationToken) =>
        ReadAsync(client.ListContainersAsync, cancellationToken);
    public Task<IReadOnlyList<DockerImageDto>> ListImagesAsync(CancellationToken cancellationToken) =>
        ReadAsync(client.ListImagesAsync, cancellationToken);
    public Task<IReadOnlyList<DockerNetworkDto>> ListNetworksAsync(CancellationToken cancellationToken) =>
        ReadAsync(client.ListNetworksAsync, cancellationToken);
    public Task<IReadOnlyList<DockerVolumeDto>> ListVolumesAsync(CancellationToken cancellationToken) =>
        ReadAsync(client.ListVolumesAsync, cancellationToken);
    public Task<IReadOnlyList<DockerStackDto>> ListStacksAsync(CancellationToken cancellationToken) =>
        ReadAsync(client.ListStacksAsync, cancellationToken);

    public Task<DockerContainerDetailsDto?> GetContainerAsync(string id, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.GetContainerAsync(id, cancellationToken), CoreAppPermissions.ServerDockerRead, cancellationToken);

    public Task<IReadOnlyList<DockerStackServiceDto>> ListStackServicesAsync(string name, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.ListStackServicesAsync(name, cancellationToken), CoreAppPermissions.ServerDockerRead, cancellationToken);

    public Task<DockerOperationResult> ApplyContainerActionAsync(string id, string action, DockerContainerActionRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.ApplyContainerActionAsync(id, action, request, cancellationToken), CoreAppPermissions.ServerDockerManage, cancellationToken);

    public Task<DockerStackPreviewDto> PreviewStackAsync(DockerStackDefinitionDto definition, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.PreviewStackAsync(definition, cancellationToken), CoreAppPermissions.ServerDockerRead, cancellationToken);

    public Task<DockerStackOperationDto> DeployStackAsync(DockerStackDeployRequest request, string idempotencyKey, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.DeployStackAsync(request, idempotencyKey, cancellationToken), CoreAppPermissions.ServerDockerManage, cancellationToken);

    public Task<DockerStackOperationDto> ApplyStackActionAsync(string name, DockerStackOperationKind action, DockerStackActionRequest request, string idempotencyKey, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.ApplyStackActionAsync(name, action, request, idempotencyKey, cancellationToken), CoreAppPermissions.ServerDockerManage, cancellationToken);

    public Task<DockerStackOperationDto?> GetStackOperationAsync(Guid operationId, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.GetStackOperationAsync(operationId, cancellationToken), CoreAppPermissions.ServerDockerRead, cancellationToken);

    public Task<DockerStackOperationDto?> GetStackOperationByRequestAsync(string requestKey, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.GetStackOperationByRequestAsync(requestKey, cancellationToken), CoreAppPermissions.ServerDockerRead, cancellationToken);

    public Task<IReadOnlyList<DockerStackOperationDto>> ListStackOperationsAsync(string name, int limit, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.ListStackOperationsAsync(name, limit, cancellationToken), CoreAppPermissions.ServerDockerRead, cancellationToken);

    public Task<DockerStackOperationDiagnosticsDto?> GetStackOperationDiagnosticsAsync(Guid operationId, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.GetStackOperationDiagnosticsAsync(operationId, cancellationToken), CoreAppPermissions.ServerDockerRead, cancellationToken);

    public Task<DockerStackOperationDto> CancelStackOperationAsync(Guid operationId, string idempotencyKey, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.CancelStackOperationAsync(operationId, idempotencyKey, cancellationToken), CoreAppPermissions.ServerDockerManage, cancellationToken);

    public Task<DockerOperationResult> PullImageAsync(DockerImageOperationRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.PullImageAsync(request, cancellationToken), CoreAppPermissions.ServerDockerManage, cancellationToken);

    public Task<DockerOperationResult> DeleteImageAsync(string id, DockerImageOperationRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.DeleteImageAsync(id, request, cancellationToken), CoreAppPermissions.ServerDockerManage, cancellationToken);

    public Task<DockerOperationResult> CreateContainerAsync(DockerContainerCreateRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.CreateContainerAsync(request, cancellationToken), CoreAppPermissions.ServerDockerManage, cancellationToken);

    public Task<DockerOperationResult> UpdateContainerAsync(string id, DockerContainerUpdateRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.UpdateContainerAsync(id, request, cancellationToken), CoreAppPermissions.ServerDockerManage, cancellationToken);

    public Task<DockerStackDefinitionDto?> GetStackDefinitionAsync(string name, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.GetStackDefinitionAsync(name, cancellationToken), CoreAppPermissions.ServerDockerRead, cancellationToken);

    public Task<DockerOperationResult> CreateNetworkAsync(DockerNetworkCreateRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.CreateNetworkAsync(request, cancellationToken), CoreAppPermissions.ServerDockerManage, cancellationToken);

    public Task<DockerOperationResult> CreateVolumeAsync(DockerVolumeCreateRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.CreateVolumeAsync(request, cancellationToken), CoreAppPermissions.ServerDockerManage, cancellationToken);

    public Task<DockerNetworkDetailsDto?> GetNetworkAsync(string id, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.GetNetworkAsync(id, cancellationToken), CoreAppPermissions.ServerDockerRead, cancellationToken);

    public Task<DockerVolumeDetailsDto?> GetVolumeAsync(string name, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.GetVolumeAsync(name, cancellationToken), CoreAppPermissions.ServerDockerRead, cancellationToken);

    public Task<DockerOperationResult> DeleteNetworkAsync(string id, bool confirmed, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.DeleteNetworkAsync(id, confirmed, cancellationToken), CoreAppPermissions.ServerDockerManage, cancellationToken);

    public Task<DockerOperationResult> DeleteVolumeAsync(string name, bool confirmed, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.DeleteVolumeAsync(name, confirmed, cancellationToken), CoreAppPermissions.ServerDockerManage, cancellationToken);

    public Task<DockerContainerLogsDto?> GetContainerLogsAsync(string id, int tail, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.GetContainerLogsAsync(id, tail, cancellationToken), CoreAppPermissions.ServerDockerRead, cancellationToken);

    public Task<DockerContainerStatsDto?> GetContainerStatsAsync(string id, CancellationToken cancellationToken) =>
        ExecuteAsync(() => client.GetContainerStatsAsync(id, cancellationToken), CoreAppPermissions.ServerDockerRead, cancellationToken);

    private Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken) =>
        ExecuteAsync(() => read(cancellationToken), CoreAppPermissions.ServerDockerRead, cancellationToken);

    private async Task<T> ExecuteAsync<T>(Func<Task<T>> execute, string permission, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAccess(permission);
        var result = await execute();
        EnsureAccess(permission);
        return result;
    }

    private void EnsureAccess(string permission)
    {
        if (!permissions.IsGranted(appId, permission))
            throw new UnauthorizedAccessException(LocalizedText.Get(permission == CoreAppPermissions.ServerDockerManage
                ? "docker.error.manage_permission" : "docker.error.read_permission"));
        if (!connection.IsCurrent || connection.Session.State != AuthSessionState.Authenticated)
            throw new InvalidOperationException(LocalizedText.Get("docker.error.not_signed_in"));
    }
}
