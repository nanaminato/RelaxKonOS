using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.FileServices;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ServerFileServicesCapability(AppId appId, IAppPermissionManager permissions,
    ExternalServerConnection connection, IServerFileServices? client, CancellationToken lifetime) : IServerFileServices
{
    private IServerFileServices Client => client ?? throw new InvalidOperationException("The file services client is unavailable.");

    public Task<FileServiceStatusDto> GetStatusAsync(CancellationToken ct) =>
        InvokeAsync(token => Client.GetStatusAsync(token), false, ct);

    public Task<FileServiceCapabilitiesDto> GetCapabilitiesAsync(CancellationToken ct) =>
        InvokeAsync(token => Client.GetCapabilitiesAsync(token), false, ct);

    public Task<IReadOnlyList<FileShareDto>> ListSharesAsync(CancellationToken ct) =>
        InvokeAsync(token => Client.ListSharesAsync(token), false, ct);

    public Task<FileServiceConnectionInfoDto> GetConnectionAsync(CancellationToken ct) =>
        InvokeAsync(token => Client.GetConnectionAsync(token), false, ct);

    public Task<FileServiceOperationResultDto> LifecycleAsync(SmbLifecycleAction action, CancellationToken ct) =>
        InvokeAsync(token => Client.LifecycleAsync(action, token), true, ct);

    public Task<FileServiceOperationResultDto> CreateShareAsync(UpsertFileShareRequest request, CancellationToken ct) =>
        InvokeAsync(token => Client.CreateShareAsync(request, token), true, ct);

    public Task<FileServiceOperationResultDto> UpdateShareAsync(string id, UpsertFileShareRequest request, CancellationToken ct) =>
        InvokeAsync(token => Client.UpdateShareAsync(id, request, token), true, ct);

    public Task<FileServiceOperationResultDto> DeleteShareAsync(string id, CancellationToken ct) =>
        InvokeAsync(token => Client.DeleteShareAsync(id, token), true, ct);

    public Task<IReadOnlyList<FileServiceUserDto>> ListUsersAsync(CancellationToken ct) =>
        InvokeAsync(token => Client.ListUsersAsync(token), false, ct);

    public Task<FileServiceOperationResultDto> SetUserEnabledAsync(string username, bool enabled, CancellationToken ct) =>
        InvokeAsync(token => Client.SetUserEnabledAsync(username, enabled, token), true, ct);

    public Task<FileServiceOperationResultDto> SetSambaPasswordAsync(string username, SetSambaPasswordRequest request, CancellationToken ct) =>
        InvokeAsync(token => Client.SetSambaPasswordAsync(username, request, token), true, ct);

    public Task<bool> ElevateAsync(HostAdministratorCredentials credentials, CancellationToken ct) =>
        InvokeAsync(token => Client.ElevateAsync(credentials, token), true, ct);

    private async Task<T> InvokeAsync<T>(Func<CancellationToken, Task<T>> operation, bool manage, CancellationToken ct)
    {
        using var linked = lifetime.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(lifetime, ct) : null;
        var token = linked?.Token ?? ct;
        CheckAccess(manage, token);
        var result = await operation(token);
        CheckAccess(manage, token);
        return result;
    }

    private void CheckAccess(bool manage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!permissions.IsGranted(appId, CoreAppPermissions.ServerFileServicesRead)
            || manage && !permissions.IsGranted(appId, CoreAppPermissions.ServerFileServicesManage))
            throw new UnauthorizedAccessException();
        if (!connection.IsCurrent)
            throw new InvalidOperationException("The application's file services connection has changed.");
    }
}
