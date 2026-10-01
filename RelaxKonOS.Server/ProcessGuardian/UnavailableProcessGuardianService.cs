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
        Task.FromException<IReadOnlyList<GuardianWorkloadDto>>(new GuardianReadException("guardian.agent_not_installed"));
    public Task<GuardianAgentResponse> GetDefinitionAsync(string workloadId, CancellationToken cancellationToken = default) => Task.FromResult(new GuardianAgentResponse(false, "guardian.agent_not_installed"));
    public Task<GuardianAgentResponse> UpsertAsync(ProcessDefinitionDto definition, CancellationToken cancellationToken = default) => Task.FromResult(new GuardianAgentResponse(false, "guardian.agent_not_installed"));
    public Task<GuardianAgentResponse> DeleteAsync(string workloadId, CancellationToken cancellationToken = default) => Task.FromResult(new GuardianAgentResponse(false, "guardian.agent_not_installed"));
    public Task<GuardianAgentResponse> ApplyActionAsync(string workloadId, string action, CancellationToken cancellationToken = default) => Task.FromResult(new GuardianAgentResponse(false, "guardian.agent_not_installed"));
    public Task<IReadOnlyList<GuardianLogEntryDto>> ListLogsAsync(string workloadId, CancellationToken cancellationToken = default) => Task.FromException<IReadOnlyList<GuardianLogEntryDto>>(new GuardianReadException("guardian.agent_not_installed"));
    public Task<IReadOnlyList<GuardianAuditEntryDto>> ListAuditAsync(CancellationToken cancellationToken = default) => Task.FromException<IReadOnlyList<GuardianAuditEntryDto>>(new GuardianReadException("guardian.agent_not_installed"));
    public Task<GuardianAgentResponse> ListScriptsAsync(string ownerIdentity, CancellationToken cancellationToken = default) => Task.FromResult(new GuardianAgentResponse(false, "guardian.agent_not_installed"));
    public Task<GuardianAgentResponse> GetScriptAsync(string ownerIdentity, string id, CancellationToken cancellationToken = default) => Task.FromResult(new GuardianAgentResponse(false, "guardian.agent_not_installed"));
    public Task<GuardianAgentResponse> SubmitScriptAsync(ScriptTaskDefinitionDto definition, CancellationToken cancellationToken = default) => Task.FromResult(new GuardianAgentResponse(false, "guardian.agent_not_installed"));
    public Task<GuardianAgentResponse> CancelScriptAsync(string ownerIdentity, string id, CancellationToken cancellationToken = default) => Task.FromResult(new GuardianAgentResponse(false, "guardian.agent_not_installed"));
}
