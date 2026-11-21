using RelaxKonOS.Protocol.Registry;

namespace RelaxKonOS.AppSDK;

/// <summary>Device-local presentation settings for the Registry workspace.</summary>
public interface IRegistryAppearancePreferences
{
    RegistryAppearanceSettings Registry { get; }
    event EventHandler? Changed;
    void Activate();
    void Deactivate();
    Task EnsureLoadedAsync();
    Task RefreshAsync();
    Task SaveSidebarWidthAsync(int width);
}

/// <summary>Permission-gated registry access scoped to the captured server connection.</summary>
public interface IServerRegistry
{
    IServerRegistry ForUser(Guid targetUserId);
    IServerRegistry ForServer(Guid instanceId);
    Task<RegistryContextDto> GetContextAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RegistryUserDto>> ListUsersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RegistryEntryDto>> ListValuesAsync(RegistryScope scope, string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RegistryKeyDto>> ListKeysAsync(RegistryScope scope, string parentPath, CancellationToken cancellationToken = default);
    Task<RegistryKeyDto> CreateKeyAsync(CreateRegistryKeyRequest request, CancellationToken cancellationToken = default);
    Task DeleteKeyAsync(RegistryScope scope, string path, CancellationToken cancellationToken = default);
    Task<RegistryEntryDto> SaveAsync(PutRegistryEntryRequest request, CancellationToken cancellationToken = default);
    Task<RegistryEntryDto> RenameAsync(RenameRegistryEntryRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsync(RegistryScope scope, string path, string name, CancellationToken cancellationToken = default);
}
