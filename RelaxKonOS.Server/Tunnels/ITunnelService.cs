using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.Server.Tunnels;

public interface ITunnelService
{
    Task<IReadOnlyList<TunnelServerProfileDto>> ListProfilesAsync(string userId, CancellationToken cancellationToken);
    Task<TunnelServerProfileDto?> GetProfileAsync(Guid id, string userId, CancellationToken cancellationToken);
    Task<TunnelServerProfileDto> UpsertProfileAsync(Guid? id, UpsertTunnelServerProfileRequest request, string userId, CancellationToken cancellationToken);
    Task<bool> DeleteProfileAsync(Guid id, string userId, CancellationToken cancellationToken);
    Task SetProfileTokenAsync(Guid id, string token, string userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TunnelDefinitionDto>> ListTunnelsAsync(string userId, CancellationToken cancellationToken);
    Task<TunnelDefinitionDto?> GetTunnelAsync(Guid id, string userId, CancellationToken cancellationToken);
    Task<TunnelDefinitionDto> UpsertTunnelAsync(Guid? id, UpsertTunnelDefinitionRequest request, string userId, CancellationToken cancellationToken);
    Task<bool> DeleteTunnelAsync(Guid id, string userId, CancellationToken cancellationToken);
}
