using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.Client.Apps.TaskManager;

/// <summary>RelaxKonOS Server 系统监控 HTTP 客户端抽象。typed HttpClient 实现（见 <see cref="TaskManagerClient"/>）。
/// 所有方法从 <c>IAuthSession</c> 取 <c>serverUrl</c> + <c>accessToken</c> 构造绝对 URI 与 Authorization 头。
/// 路由常量见 <see cref="SystemMonitorApiRoutes"/>。错误统一为 <see cref="RemoteOsAuthException"/>（含 ProblemDetails）。</summary>
public interface ITaskManagerClient
{
    /// <summary>获取性能页低频信息与能力。</summary>
    Task<PerformanceInfoDto> GetPerformanceInfoAsync(CancellationToken ct = default);

    /// <summary>获取最近一份有效性能快照（作为实时 Hub 的降级路径）。</summary>
    Task<PerformanceRealtimeSnapshotDto> GetPerformanceSnapshotAsync(CancellationToken ct = default);

    /// <summary>获取服务端短期性能历史（最多 60 秒）。</summary>
    Task<IReadOnlyList<PerformanceRealtimeSnapshotDto>> GetPerformanceHistoryAsync(int seconds = 60, CancellationToken ct = default);

    /// <summary>获取服务器所有可用的非回环 IPv4/IPv6 地址。</summary>
    Task<IReadOnlyList<NetworkAddressDto>> GetNetworkAddressesAsync(CancellationToken ct = default);

    /// <summary>按页、过滤和排序查询进程；性能页不会调用此方法。</summary>
    Task<ProcessPageDto> QueryProcessesAsync(int page = 1, int pageSize = 100, string? filter = null,
        string? sort = null, bool descending = true, CancellationToken ct = default);

    /// <summary>结束指定进程。force=true 强制终止。权限不足返回 RequiresElevation=true。</summary>
    Task<KillProcessResultDto> KillProcessAsync(int processId, bool force = false, CancellationToken ct = default);
}
