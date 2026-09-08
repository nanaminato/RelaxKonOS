using RelaxKonOS.Protocol.ProcessGuardian;

namespace RelaxKonOS.Server.ProcessGuardian;

/// <summary>Server facade for the separate Guardian Agent. It never starts workloads itself.</summary>
public interface IProcessGuardianService
{
    Task<GuardianStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GuardianWorkloadDto>> ListWorkloadsAsync(CancellationToken cancellationToken = default);
    Task<GuardianAgentResponse> GetDefinitionAsync(string workloadId, CancellationToken cancellationToken = default);
    Task<GuardianAgentResponse> UpsertAsync(ProcessDefinitionDto definition, CancellationToken cancellationToken = default);
    Task<GuardianAgentResponse> DeleteAsync(string workloadId, CancellationToken cancellationToken = default);
    Task<GuardianAgentResponse> ApplyActionAsync(string workloadId, string action, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GuardianLogEntryDto>> ListLogsAsync(string workloadId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GuardianAuditEntryDto>> ListAuditAsync(CancellationToken cancellationToken = default);
}
