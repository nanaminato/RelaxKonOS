using RelaxKonOS.Protocol.ProcessGuardian;

namespace RelaxKonOS.Client.Apps.ProcessGuardian;

public interface IProcessGuardianClient
{
    Task<GuardianStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GuardianWorkloadDto>> ListWorkloadsAsync(CancellationToken cancellationToken = default);
    Task<GuardianAgentResponse> GetDefinitionAsync(string id, CancellationToken cancellationToken = default);
    Task<GuardianAgentResponse> UpsertAsync(UpsertGuardianWorkloadRequest request, CancellationToken cancellationToken = default);
    Task<GuardianAgentResponse> DeleteAsync(string id, CancellationToken cancellationToken = default);
    Task<GuardianAgentResponse> ApplyActionAsync(string id, string action, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GuardianLogEntryDto>> ListLogsAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GuardianAuditEntryDto>> ListAuditAsync(CancellationToken cancellationToken = default);
}
