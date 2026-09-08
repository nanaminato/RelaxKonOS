using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.Server.SystemPerformance;

/// <summary>独立于性能页的进程采样和控制服务。</summary>
public interface IProcessService
{
    Task<ProcessPageDto> QueryAsync(int page, int pageSize, string? filter, string? sort, bool descending, CancellationToken cancellationToken = default);

    Task<KillProcessResultDto> KillAsync(int processId, bool force, CancellationToken cancellationToken = default);
}
