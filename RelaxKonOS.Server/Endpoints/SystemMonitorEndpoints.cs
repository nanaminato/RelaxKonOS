using RelaxKonOS.Protocol.SystemMonitor;
using RelaxKonOS.Server.SystemPerformance;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>系统监控（任务管理器）REST 端点。路由常量见 <see cref="SystemMonitorApiRoutes"/>。所有端点需 JWT（[Authorize]）。
/// 数据采集由 <see cref="RelaxKonOS.Server.SystemMonitor.ISystemMetricsProvider"/> 完成（以宿主 OS 进程身份，复用宿主用户/权限）。
/// 错误统一返回 RFC 7807 ProblemDetails（type URI 作错误码）。
/// 这些路由归 <c>relaxkonos.system-performance</c> 模块所有；宿主网络地址属于 Core，见 <see cref="HostNetworkEndpoints"/>。</summary>
public static class SystemMonitorEndpoints
{
    private const string ProblemBase = "https://relaxkonos.app/problems/";

    /// <summary>
    /// 一次性快照读取为自己申请的采样窗口长度。只有 demand 的采集需要两个采样周期才会产出有效样本
    /// （第一次原始读取只建立差分基线），所以窗口覆盖两个周期并留出余量；仍取不到样本才返回 503。
    /// </summary>
    private static readonly TimeSpan SnapshotWait = TimeSpan.FromSeconds(3);

    public static IEndpointRouteBuilder MapSystemMonitorEndpoints(this IEndpointRouteBuilder app)
    {
        // 新性能链路：静态信息、有效快照和 60 秒内存历史分离；客户端通过 PerformanceHub 获取实时更新。
        app.MapGet(SystemMonitorApiRoutes.PerformanceInfo,
            async (IPerformanceSampler sampler, CancellationToken ct) => Results.Ok(await sampler.GetInfoAsync(ct)))
           .RequireAuthorization()
           .WithTags("System Performance");

        // 快照是首进、重连与无实时订阅客户端的降级路径，因此读取本身构成 demand：取不到样本才 503。
        app.MapGet(SystemMonitorApiRoutes.PerformanceSnapshot,
            async (IPerformanceSampler sampler, CancellationToken ct) =>
                await sampler.ReadSnapshotAsync(SnapshotWait, ct) is { } snapshot
                    ? Results.Ok(snapshot)
                    : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Performance sample is not ready.", type: ProblemBase + "performance-not-ready"))
           .RequireAuthorization()
           .WithTags("System Performance");

        app.MapGet(SystemMonitorApiRoutes.PerformanceHistory,
            (int? seconds, IPerformanceSampler sampler) => Results.Ok(sampler.GetHistory(seconds ?? 60)))
           .RequireAuthorization()
           .WithTags("System Performance");

        app.MapGet(SystemMonitorApiRoutes.ProcessQuery,
            async (int? page, int? pageSize, string? filter, string? sort, string? direction, bool? elevated,
                HttpContext http, IHostElevationSessionStore grants, IProcessService processes, CancellationToken ct) =>
            {
                if (elevated == true && !grants.IsGranted(http.User, HostElevationCapability.ProcessManage, "host/processes"))
                    return ElevationRequired();
                try
                {
                    return Results.Ok(await processes.QueryAsync(page ?? 1, pageSize ?? 100,
                        filter, sort, !string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase), elevated == true, ct));
                }
                catch (ProcessSnapshotUnavailableException error)
                {
                    return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Process snapshot is unavailable.", detail: error.Message,
                        type: ProblemBase + "process-snapshot-unavailable");
                }
            })
           .RequireAuthorization()
           .WithTags("System Performance");

        app.MapGet(SystemMonitorApiRoutes.ProcessIcon,
            async (int id, DateTimeOffset startTime, bool? elevated, HttpContext http, IHostElevationSessionStore grants,
                IProcessService processes, CancellationToken ct) =>
            {
                if (elevated == true && !grants.IsGranted(http.User, HostElevationCapability.ProcessManage, "host/processes"))
                    return ElevationRequired();
                return await processes.GetIconAsync(id, startTime, elevated == true, ct) is { Data.Length: > 0 } icon
                    ? Results.File(icon.Data, icon.ContentType) : Results.NotFound();
            })
           .RequireAuthorization()
           .WithTags("System Performance");

        // DELETE system/processes/{id} with required expectedStartTime — terminate the confirmed instance.
        app.MapDelete(SystemMonitorApiRoutes.ProcessKill,
            async (int id, [Microsoft.AspNetCore.Mvc.FromBody] TerminateProcessRequest request, HttpContext http,
                IHostElevationSessionStore grants, IProcessService processes, CancellationToken ct) =>
            {
                if (request.Elevated && !grants.IsGranted(http.User, HostElevationCapability.ProcessManage, "host/processes"))
                    return ElevationRequired();
                return Results.Ok(await processes.KillAsync(id, request.ExpectedStartTime, request.Elevated, ct));
            })
           .RequireAuthorization()
           .WithTags("System");

        return app;
    }

    private static IResult ElevationRequired() => Results.Problem(statusCode: StatusCodes.Status403Forbidden,
        title: "Process administrator mode requires elevation.", type: ProblemBase + "elevation-required",
        extensions: new Dictionary<string, object?> { ["problemCode"] = "elevation-required" });
}
