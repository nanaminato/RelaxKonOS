using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Docker;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Docker;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ServerDockerInventoryCapability(AppId appId, IAppPermissionManager permissions,
    UsageMemoryScope connection, IRemoteDockerClient client) : IServerDockerInventory
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

    private async Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAccess();
        var result = await read(cancellationToken);
        EnsureAccess();
        return result;
    }

    private void EnsureAccess()
    {
        if (!permissions.IsGranted(appId, CoreAppPermissions.ServerDockerRead))
            throw new UnauthorizedAccessException(LocalizedText.Get("docker.error.read_permission"));
        if (!connection.IsCurrent || connection.Session.State != AuthSessionState.Authenticated)
            throw new InvalidOperationException(LocalizedText.Get("docker.error.not_signed_in"));
    }
}
