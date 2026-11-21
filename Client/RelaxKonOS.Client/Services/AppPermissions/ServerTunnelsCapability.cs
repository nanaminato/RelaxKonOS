using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Tunnels;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ServerTunnelsCapability(AppId appId, IAppPermissionManager permissions,
    ExternalServerConnection connection, IServerTunnels? client, CancellationToken lifetime) : IServerTunnels
{
    private IServerTunnels Client => client ?? throw new InvalidOperationException("Server tunnels are unavailable.");

    public Task<IReadOnlyList<TunnelServerProfileDto>> ListProfilesAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(requestToken => Client.ListProfilesAsync(requestToken), manage: false, cancellationToken);

    public Task<TunnelServerProfileDto?> GetProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        InvokeAsync(requestToken => Client.GetProfileAsync(profileId, requestToken), manage: true, cancellationToken);

    public Task<IReadOnlyList<TunnelDefinitionDto>> ListAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(requestToken => Client.ListAsync(requestToken), manage: false, cancellationToken);

    public Task<TunnelRuntimeDto> GetRuntimeAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(requestToken => Client.GetRuntimeAsync(requestToken), manage: false, cancellationToken);

    public Task<TunnelRuntimeDownloadDto?> GetManagedRuntimeDownloadAsync(string version, CancellationToken cancellationToken = default) =>
        InvokeAsync(requestToken => Client.GetManagedRuntimeDownloadAsync(version, requestToken), manage: false, cancellationToken);

    public Task<TunnelServerProfileDto> CreateProfileAsync(UpsertTunnelServerProfileRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync(requestToken => Client.CreateProfileAsync(request, requestToken), manage: true, cancellationToken);

    public Task<TunnelServerProfileDto> UpdateProfileAsync(Guid profileId, UpsertTunnelServerProfileRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync(requestToken => Client.UpdateProfileAsync(profileId, request, requestToken), manage: true, cancellationToken);

    public Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        InvokeAsync(requestToken => Client.DeleteProfileAsync(profileId, requestToken), manage: true, cancellationToken);

    public Task SetProfileTokenAsync(Guid profileId, string token, CancellationToken cancellationToken = default) =>
        InvokeAsync(requestToken => Client.SetProfileTokenAsync(profileId, token, requestToken), manage: true, cancellationToken);

    public Task<TunnelDefinitionDto> CreateTunnelAsync(UpsertTunnelDefinitionRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync(requestToken => Client.CreateTunnelAsync(request, requestToken), manage: true, cancellationToken);

    public Task<TunnelDefinitionDto> UpdateTunnelAsync(Guid tunnelId, UpsertTunnelDefinitionRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync(requestToken => Client.UpdateTunnelAsync(tunnelId, request, requestToken), manage: true, cancellationToken);

    public Task DeleteTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default) =>
        InvokeAsync(requestToken => Client.DeleteTunnelAsync(tunnelId, requestToken), manage: true, cancellationToken);

    public Task<TunnelOperationResultDto> ApplyAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        InvokeAsync(requestToken => Client.ApplyAsync(profileId, requestToken), manage: true, cancellationToken);

    public Task<TunnelOperationResultDto> StopAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        InvokeAsync(requestToken => Client.StopAsync(profileId, requestToken), manage: true, cancellationToken);

    public Task<IReadOnlyList<TunnelLogEntryDto>> GetLogsAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        InvokeAsync(requestToken => Client.GetLogsAsync(profileId, requestToken), manage: false, cancellationToken);

    public Task<TunnelRuntimeDto> DetectExternalRuntimeAsync(string executablePath, CancellationToken cancellationToken = default) =>
        InvokeAsync(requestToken => Client.DetectExternalRuntimeAsync(executablePath, requestToken), manage: true, cancellationToken);

    public Task<ManagedFrpsConfigurationDto> GetManagedFrpsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(requestToken => Client.GetManagedFrpsAsync(requestToken), manage: false, cancellationToken);

    public Task<ManagedFrpsConfigurationDto> GetManagedFrpsForEditingAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(requestToken => Client.GetManagedFrpsForEditingAsync(requestToken), manage: true, cancellationToken);

    public Task<ManagedFrpsConfigurationDto> UpdateManagedFrpsAsync(UpdateManagedFrpsConfigurationRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync(requestToken => Client.UpdateManagedFrpsAsync(request, requestToken), manage: true, cancellationToken);

    public Task<TunnelOperationResultDto> StartManagedFrpsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(requestToken => Client.StartManagedFrpsAsync(requestToken), manage: true, cancellationToken);

    public Task<TunnelOperationResultDto> StopManagedFrpsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(requestToken => Client.StopManagedFrpsAsync(requestToken), manage: true, cancellationToken);

    public Task<IReadOnlyList<TunnelLogEntryDto>> GetManagedFrpsLogsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(requestToken => Client.GetManagedFrpsLogsAsync(requestToken), manage: false, cancellationToken);

    public Task<IReadOnlyList<TunnelAuditEntryDto>> GetManagedFrpsAuditAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(requestToken => Client.GetManagedFrpsAuditAsync(requestToken), manage: false, cancellationToken);

    private async Task<T> InvokeAsync<T>(Func<CancellationToken, Task<T>> operation, bool manage, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        var token = linked.Token;
        CheckAccess(manage, token);
        using var observation = connection.ObserveInvalidation(linked.Cancel);
        token.ThrowIfCancellationRequested();
        var result = await operation(token);
        CheckAccess(manage, token);
        return result;
    }

    private Task InvokeAsync(Func<CancellationToken, Task> operation, bool manage, CancellationToken cancellationToken) =>
        InvokeAsync(async token =>
        {
            await operation(token);
            return true;
        }, manage, cancellationToken);

    private void CheckAccess(bool manage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!connection.IsCurrent)
            throw new InvalidOperationException("The application's tunnel connection has changed.");
        if (!permissions.IsGranted(appId, CoreAppPermissions.ServerTunnelsRead)
            || manage && !permissions.IsGranted(appId, CoreAppPermissions.ServerTunnelsManage))
            throw new UnauthorizedAccessException();
    }
}
