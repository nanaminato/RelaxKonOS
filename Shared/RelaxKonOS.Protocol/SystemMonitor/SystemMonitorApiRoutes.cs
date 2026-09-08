using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.SystemMonitor;

/// <summary>系统监控（任务管理器）REST 端点路由常量。路径已含 /api/v1.0 前缀。Server 注册路由与 Client 拼接 URL 共用。
/// 所有端点需 JWT（[Authorize]）。错误统一返回 RFC 7807 ProblemDetails。</summary>
public static class SystemMonitorApiRoutes
{
    private const string V1 = RelaxKonOSEndpoints.ApiVersionPrefix;

    /// <summary>获取低频系统性能信息与能力（GET，需 JWT）。</summary>
    public const string PerformanceInfo = $"/{V1}/system/performance/info";

    /// <summary>获取最近一份有效性能快照（GET，需 JWT）。</summary>
    public const string PerformanceSnapshot = $"/{V1}/system/performance/snapshot";

    /// <summary>获取短期内存性能历史（GET，需 JWT，seconds 上限 60）。</summary>
    public const string PerformanceHistory = $"/{V1}/system/performance/history";

    /// <summary>分页、过滤、排序的进程查询端点（GET，需 JWT）。</summary>
    public const string ProcessQuery = $"/{V1}/system/processes/query";

    /// <summary>获取服务端所有可用的非回环 IPv4/IPv6 地址（GET，需 JWT）。</summary>
    public const string NetworkAddresses = $"/{V1}/system/network-addresses";

    /// <summary>结束进程（DELETE，需 JWT）。route: id。返回 KillProcessResultDto。</summary>
    public const string ProcessKill = $"/{V1}/system/processes/{{id}}";
}
