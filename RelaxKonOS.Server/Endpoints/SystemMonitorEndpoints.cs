using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Claims;
using RelaxKonOS.Protocol.SystemMonitor;
using RelaxKonOS.Server.SystemPerformance;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>系统监控（任务管理器）REST 端点。路由常量见 <see cref="SystemMonitorApiRoutes"/>。所有端点需 JWT（[Authorize]）。
/// 数据采集由 <see cref="RelaxKonOS.Server.SystemMonitor.ISystemMetricsProvider"/> 完成（以宿主 OS 进程身份，复用宿主用户/权限）。
/// 错误统一返回 RFC 7807 ProblemDetails（type URI 作错误码）。</summary>
public static class SystemMonitorEndpoints
{
    private const string ProblemBase = "https://relaxkonos.app/problems/";

    public static IEndpointRouteBuilder MapSystemMonitorEndpoints(this IEndpointRouteBuilder app)
    {
        // 新性能链路：静态信息、有效快照和 60 秒内存历史分离；客户端通过 PerformanceHub 获取实时更新。
        app.MapGet(SystemMonitorApiRoutes.PerformanceInfo,
            async (IPerformanceSampler sampler, CancellationToken ct) => Results.Ok(await sampler.GetInfoAsync(ct)))
           .RequireAuthorization()
           .WithTags("System Performance");

        app.MapGet(SystemMonitorApiRoutes.PerformanceSnapshot,
            (IPerformanceSampler sampler) => sampler.GetLatest() is { } snapshot
                ? Results.Ok(snapshot)
                : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Performance sample is not ready.", type: ProblemBase + "performance-not-ready"))
           .RequireAuthorization()
           .WithTags("System Performance");

        app.MapGet(SystemMonitorApiRoutes.PerformanceHistory,
            (int? seconds, IPerformanceSampler sampler) => Results.Ok(sampler.GetHistory(seconds ?? 60)))
           .RequireAuthorization()
           .WithTags("System Performance");

        app.MapGet(SystemMonitorApiRoutes.NetworkAddresses, () => Results.Ok(GetNetworkAddresses()))
           .RequireAuthorization()
           .WithTags("System");

        app.MapGet(SystemMonitorApiRoutes.ProcessQuery,
            async (int? page, int? pageSize, string? filter, string? sort, string? direction,
                IProcessService processes, CancellationToken ct) => Results.Ok(await processes.QueryAsync(page ?? 1, pageSize ?? 100,
                    filter, sort, !string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase), ct)))
           .RequireAuthorization()
           .WithTags("System Performance");

        // DELETE system/processes/{id}?force= — 结束进程；权限不足返回 requiresElevation
        app.MapDelete(SystemMonitorApiRoutes.ProcessKill,
            (int id, bool? force, IProcessService processes, CancellationToken ct)
                => processes.KillAsync(id, force ?? false, ct))
           .RequireAuthorization()
           .WithTags("System");

        return app;
    }

    private static IReadOnlyList<NetworkAddressDto> GetNetworkAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up &&
                                  network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(network => network.GetIPProperties().UnicastAddresses
                    .Select(unicast => (network.Name, Address: unicast.Address)))
                .Where(item => item.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Where(item => !IPAddress.IsLoopback(item.Address) && !item.Address.IsIPv6LinkLocal)
                .Select(item => new NetworkAddressDto(
                    item.Name,
                    item.Address.ToString(),
                    item.Address.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6"))
                .DistinctBy(item => (item.Family, item.Address))
                .OrderBy(item => item.Family)
                .ThenBy(item => item.Address, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return Array.Empty<NetworkAddressDto>();
        }
    }
}
