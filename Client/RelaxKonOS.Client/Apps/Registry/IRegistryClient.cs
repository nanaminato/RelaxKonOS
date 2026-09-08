using RelaxKonOS.Protocol.Registry;

namespace RelaxKonOS.Client.Apps.Registry;

/// <summary>Host-only client for the schema-approved registry read model.</summary>
public interface IRegistryClient
{
    Task<IReadOnlyList<RegistryEntryDto>> ListValuesAsync(RegistryScope scope, string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RegistryKeyDto>> ListKeysAsync(RegistryScope scope, string parentPath, CancellationToken cancellationToken = default);
    Task<RegistryKeyDto> CreateKeyAsync(CreateRegistryKeyRequest request, CancellationToken cancellationToken = default);
    Task DeleteKeyAsync(RegistryScope scope, string path, CancellationToken cancellationToken = default);
    Task<RegistryEntryDto> SaveAsync(PutRegistryEntryRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsync(RegistryScope scope, string path, string name, CancellationToken cancellationToken = default);
}
