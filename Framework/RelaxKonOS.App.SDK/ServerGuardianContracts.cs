using RelaxKonOS.Protocol.ProcessGuardian;

namespace RelaxKonOS.AppSDK;

public interface IServerGuardian
{
    Task<GuardianStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GuardianWorkloadDto>> ListWorkloadsAsync(CancellationToken cancellationToken = default);
    Task<GuardianAgentResponse> GetDefinitionAsync(string id, CancellationToken cancellationToken = default);
    Task<GuardianAgentResponse> UpsertAsync(UpsertGuardianWorkloadRequest request, CancellationToken cancellationToken = default);
    Task<GuardianAgentResponse> DeleteAsync(string id, CancellationToken cancellationToken = default);
    Task<GuardianAgentResponse> ApplyActionAsync(string id, string action, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GuardianLogEntryDto>> ListLogsAsync(string id, CancellationToken cancellationToken = default);
    IAsyncEnumerable<IReadOnlyList<GuardianLogEntryDto>> WatchLogsAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GuardianAuditEntryDto>> ListAuditAsync(CancellationToken cancellationToken = default);
}

/// <summary>Desktop interactions for the official Process Guardian package.</summary>
public interface IGuardianHostInteraction
{
    string? CurrentUsername { get; }
    Task<string?> PickWorkloadPathAsync(IExternalAppWindowHandle owner, string title, GuardianWorkloadPathKind kind);
}

public enum GuardianWorkloadPathKind { Executable, WorkingDirectory }
