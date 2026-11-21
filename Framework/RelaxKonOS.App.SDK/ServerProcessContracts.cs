using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.AppSDK;

/// <summary>Server process operations scoped to the application grant and authenticated connection.</summary>
public interface IServerProcesses
{
    Task<ProcessPageDto> QueryAsync(int page = 1, int pageSize = 100, string? filter = null,
        string? sort = null, bool descending = true, bool administratorMode = false, CancellationToken cancellationToken = default);

    Task<(byte[] Data, string ContentType)?> GetIconAsync(int processId, DateTimeOffset startTime,
        bool administratorMode = false, CancellationToken cancellationToken = default);

    /// <summary>Uses the host's authorization flow to probe administrator access with a read.</summary>
    Task AuthorizeAdministratorAsync(CancellationToken cancellationToken = default);

    /// <summary>Terminates the specific process instance identified by its PID and start time.</summary>
    Task<KillProcessResultDto> TerminateAsync(int processId, DateTimeOffset expectedStartTime,
        bool administratorMode = false, CancellationToken cancellationToken = default);
}
