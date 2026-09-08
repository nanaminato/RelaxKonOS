using RelaxKonOS.Protocol.ProcessGuardian;

namespace RelaxKonOS.Server.ProcessGuardian;

/// <summary>
/// Safe initial provider until the mutually-authenticated Guardian Agent IPC is installed.
/// Returning an explicit unavailable state prevents the Server process from accidentally
/// becoming a workload supervisor, which would violate the restart and login boundaries.
/// </summary>
public sealed class UnavailableProcessGuardianService : IProcessGuardianService
{
    public Task<GuardianStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new GuardianStatusDto(false, false, "guardian.agent_not_installed", null));

    public Task<IReadOnlyList<GuardianWorkloadDto>> ListWorkloadsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GuardianWorkloadDto>>(Array.Empty<GuardianWorkloadDto>());
    public Task<GuardianAgentResponse> GetDefinitionAsync(string workloadId, CancellationToken cancellationToken = default) => Task.FromResult(new GuardianAgentResponse(false, "guardian.agent_not_installed"));
    public Task<GuardianAgentResponse> UpsertAsync(ProcessDefinitionDto definition, CancellationToken cancellationToken = default) => Task.FromResult(new GuardianAgentResponse(false, "guardian.agent_not_installed"));
    public Task<GuardianAgentResponse> DeleteAsync(string workloadId, CancellationToken cancellationToken = default) => Task.FromResult(new GuardianAgentResponse(false, "guardian.agent_not_installed"));
    public Task<GuardianAgentResponse> ApplyActionAsync(string workloadId, string action, CancellationToken cancellationToken = default) => Task.FromResult(new GuardianAgentResponse(false, "guardian.agent_not_installed"));
    public Task<IReadOnlyList<GuardianLogEntryDto>> ListLogsAsync(string workloadId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GuardianLogEntryDto>>(Array.Empty<GuardianLogEntryDto>());
    public Task<IReadOnlyList<GuardianAuditEntryDto>> ListAuditAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GuardianAuditEntryDto>>(Array.Empty<GuardianAuditEntryDto>());
}
