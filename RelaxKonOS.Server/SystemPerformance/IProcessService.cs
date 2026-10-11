using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.Server.SystemPerformance;

/// <summary>独立于性能页的进程采样和控制服务。</summary>
public interface IProcessService
{
    Task<ProcessPageDto> QueryAsync(int page, int pageSize, string? filter, string? sort, bool descending, bool elevated = false, CancellationToken cancellationToken = default);

    Task<ProcessIconContent?> GetIconAsync(int processId, DateTimeOffset expectedStartTime, bool elevated = false, CancellationToken cancellationToken = default);

    Task<KillProcessResultDto> KillAsync(int processId, DateTimeOffset expectedStartTime, bool elevated = false, CancellationToken cancellationToken = default);
}

public sealed record ProcessIconContent(byte[] Data, string ContentType);
