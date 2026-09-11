using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.Client.Apps.Tunnels;

public interface IRemoteTunnelClient
{
    Task<IReadOnlyList<TunnelServerProfileDto>> ListProfilesAsync(CancellationToken cancellationToken = default);
    Task<TunnelServerProfileDto?> GetProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TunnelDefinitionDto>> ListAsync(CancellationToken cancellationToken = default);
    Task<TunnelRuntimeDto> GetRuntimeAsync(CancellationToken cancellationToken = default);
    Task<TunnelRuntimeDownloadDto?> GetManagedRuntimeDownloadAsync(string version, CancellationToken cancellationToken = default);
    Task<TunnelServerProfileDto> CreateProfileAsync(UpsertTunnelServerProfileRequest request, CancellationToken cancellationToken = default);
    Task<TunnelServerProfileDto> UpdateProfileAsync(Guid profileId, UpsertTunnelServerProfileRequest request, CancellationToken cancellationToken = default);
    Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task SetProfileTokenAsync(Guid profileId, string token, CancellationToken cancellationToken = default);
    Task<TunnelDefinitionDto> CreateTunnelAsync(UpsertTunnelDefinitionRequest request, CancellationToken cancellationToken = default);
    Task<TunnelDefinitionDto> UpdateTunnelAsync(Guid tunnelId, UpsertTunnelDefinitionRequest request, CancellationToken cancellationToken = default);
    Task DeleteTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default);
    Task<TunnelOperationResultDto> ApplyAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<TunnelOperationResultDto> StopAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TunnelLogEntryDto>> GetLogsAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<TunnelRuntimeDto> DetectExternalRuntimeAsync(string executablePath, CancellationToken cancellationToken = default);
    Task<ManagedFrpsConfigurationDto> GetManagedFrpsAsync(CancellationToken cancellationToken = default);
    Task<ManagedFrpsConfigurationDto> GetManagedFrpsForEditingAsync(CancellationToken cancellationToken = default);
    Task<ManagedFrpsConfigurationDto> UpdateManagedFrpsAsync(UpdateManagedFrpsConfigurationRequest request, CancellationToken cancellationToken = default);
    Task<TunnelOperationResultDto> StartManagedFrpsAsync(CancellationToken cancellationToken = default);
    Task<TunnelOperationResultDto> StopManagedFrpsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TunnelLogEntryDto>> GetManagedFrpsLogsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TunnelAuditEntryDto>> GetManagedFrpsAuditAsync(CancellationToken cancellationToken = default);
}
