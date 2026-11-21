using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Registry;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Registry;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ServerRegistryCapability(AppId appId, IAppPermissionManager permissions,
    ExternalServerConnection connection, IRegistryClient? client) : IServerRegistry
{
    private IRegistryClient Client => client ?? throw new InvalidOperationException("The registry client is unavailable.");

    public IServerRegistry ForUser(Guid targetUserId) => Bind(registry => registry.ForUser(targetUserId));
    public IServerRegistry ForServer(Guid instanceId) => Bind(registry => registry.ForServer(instanceId));

    private IServerRegistry Bind(Func<IRegistryClient, IRegistryClient> bind)
    {
        if (!connection.IsCurrent) throw new InvalidOperationException("The application's server connection has changed.");
        return new ServerRegistryCapability(appId, permissions, connection, bind(Client));
    }

    public Task<RegistryContextDto> GetContextAsync(CancellationToken token) =>
        RunAsync(() => Client.GetContextAsync(token), CoreAppPermissions.ServerRegistryRead, token);

    public Task<IReadOnlyList<RegistryUserDto>> ListUsersAsync(CancellationToken token) =>
        RunAsync(() => Client.ListUsersAsync(token), CoreAppPermissions.ServerRegistryRead, token);

    public Task<IReadOnlyList<RegistryEntryDto>> ListValuesAsync(RegistryScope scope, string path, CancellationToken token) =>
        RunAsync(() => Client.ListValuesAsync(scope, path, token), CoreAppPermissions.ServerRegistryRead, token);

    public Task<IReadOnlyList<RegistryKeyDto>> ListKeysAsync(RegistryScope scope, string parentPath, CancellationToken token) =>
        RunAsync(() => Client.ListKeysAsync(scope, parentPath, token), CoreAppPermissions.ServerRegistryRead, token);

    public Task<RegistryEntryDto> SaveAsync(PutRegistryEntryRequest request, CancellationToken token) =>
        RunAsync(() => Client.SaveAsync(request, token), CoreAppPermissions.ServerRegistryWrite, token);

    public Task<RegistryEntryDto> RenameAsync(RenameRegistryEntryRequest request, CancellationToken token) =>
        RunAsync(() => Client.RenameAsync(request, token), CoreAppPermissions.ServerRegistryWrite, token);

    public Task<RegistryKeyDto> CreateKeyAsync(CreateRegistryKeyRequest request, CancellationToken token) =>
        RunAsync(() => Client.CreateKeyAsync(request, token), CoreAppPermissions.ServerRegistryWrite, token);

    public Task DeleteAsync(RegistryScope scope, string path, string name, CancellationToken token) =>
        RunAsync(async () => { await Client.DeleteAsync(scope, path, name, token); return true; }, CoreAppPermissions.ServerRegistryWrite, token);

    public Task DeleteKeyAsync(RegistryScope scope, string path, CancellationToken token) =>
        RunAsync(async () => { await Client.DeleteKeyAsync(scope, path, token); return true; }, CoreAppPermissions.ServerRegistryWrite, token);

    private async Task<T> RunAsync<T>(Func<Task<T>> operation, string permission, CancellationToken token)
    {
        CheckAccess(permission, token);
        var result = await operation();
        CheckAccess(permission, token);
        return result;
    }

    private void CheckAccess(string permission, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!permissions.IsGranted(appId, permission)) throw new UnauthorizedAccessException();
        if (!connection.IsCurrent) throw new InvalidOperationException("The application's server connection has changed.");
    }
}
