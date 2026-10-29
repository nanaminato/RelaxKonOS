using System.Runtime.InteropServices;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Hubs;
using RelaxKonOS.Server.SystemMonitor;
using RelaxKonOS.Server.SystemPerformance;

namespace RelaxKonOS.Server.Startup.Modules;

/// <summary>
/// Host performance sampling and process control: the OS samplers, the background sampler, its
/// SignalR projection, the performance/process REST routes and the performance Hub form one owner.
/// Disabling it removes the background sampling cost entirely instead of leaving a 1-second timer
/// feeding a Hub that nobody can reach.
///
/// Host network addresses are deliberately *not* here: they answer a Core question (which addresses
/// can a client reach this host on) and are also used by applications that never read metrics.
/// </summary>
internal sealed class SystemPerformanceModule : IServerModule
{
    public string Id => ServerModules.SystemPerformanceId;
    public IReadOnlyList<string> Capabilities => [ServerCapabilities.Metrics, ServerCapabilities.Processes];
    public IReadOnlyList<string> Requires => [];

    public void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        // 采集 Provider 按宿主 OS 平台选择，与 IIdentityProvider 同模式。以宿主 OS 进程身份读取，
        // 复用宿主用户/权限（不另建 ACL）。Singleton 持相邻采样差分状态。
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            builder.Services.AddSingleton<ISystemMetricsProvider, WindowsMetricsProvider>();
        else
            builder.Services.AddSingleton<ISystemMetricsProvider, LinuxMetricsProvider>();

        // 统一 1 秒原始读取；短期历史和 Hub 广播各自分层。
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            builder.Services.AddSingleton<ISystemPerformanceSource, WindowsPerformanceSource>();
        else
            builder.Services.AddSingleton<ISystemPerformanceSource, LinuxPerformanceSource>();
        builder.Services.AddSingleton<PerformanceHistory>();
        builder.Services.AddSingleton<PerformanceSubscriptionRegistry>();
        builder.Services.AddSingleton<PerformanceSampler>();
        builder.Services.AddSingleton<IPerformanceSampler>(services => services.GetRequiredService<PerformanceSampler>());
        builder.Services.AddHostedService(services => services.GetRequiredService<PerformanceSampler>());
        builder.Services.AddSingleton<ProcessSampler>();
        builder.Services.AddSingleton<IProcessService>(services => services.GetRequiredService<ProcessSampler>());
        // 订阅推送只服务本模块映射的 Hub；禁用时随模块一起消失。
        builder.Services.AddHostedService<PerformanceBroadcastService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        endpoints.MapSystemMonitorEndpoints();
        endpoints.MapHub<PerformanceHub>(RelaxKonOSEndpoints.PerformanceHubPath, options => options.CloseOnAuthenticationExpiration = true);
    }
}
