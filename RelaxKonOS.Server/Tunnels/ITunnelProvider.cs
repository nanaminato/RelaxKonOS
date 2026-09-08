using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.Server.Tunnels;

/// <summary>Provider boundary. FRP is an implementation detail rather than an application-wide dependency.</summary>
public interface ITunnelProvider
{
    string ProviderId { get; }
    Task<TunnelRuntimeDto> GetStatusAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TunnelDefinitionDto>> ListAsync(string userId, CancellationToken cancellationToken);
    Task<TunnelOperationResultDto> ApplyAsync(Guid profileId, string userId, CancellationToken cancellationToken);
    Task<TunnelOperationResultDto> StopAsync(Guid profileId, string userId, CancellationToken cancellationToken);
    Task StopManagedProcessesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TunnelLogEntryDto>?> GetLogsAsync(Guid profileId, string userId, CancellationToken cancellationToken);
}
