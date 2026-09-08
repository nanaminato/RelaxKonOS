using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.Server.SystemMonitor;

/// <summary>宿主 OS 系统指标采集抽象（任务管理器后端）。与 <c>IIdentityProvider</c> 同模式：
/// 平台差异封装在 Provider 之后，Server 端单一代码库跨 Ubuntu(Windows) + Windows Server。
/// 实现为 Singleton（持相邻采样差分状态以计算 CPU% 与网络速率）。所有数据以宿主 OS 进程身份读取
/// （复用宿主用户/权限，不另建 ACL——见 project_memory 硬约束）。</summary>
public interface ISystemMetricsProvider
{
    /// <summary>采集整机资源占用快照（CPU/内存/磁盘/网络/GPU/运行时间）。</summary>
    Task<SystemMetricsDto> GetMetricsAsync(CancellationToken ct = default);

    /// <summary>列举当前可见进程（含每进程 CPU% 与内存）。CPU% 由相邻采样差分计算，首次调用为 0。</summary>
    Task<IReadOnlyList<ProcessInfoDto>> ListProcessesAsync(CancellationToken ct = default);

    /// <summary>结束指定进程。权限不足时返回 <c>RequiresElevation=true</c>（RelaxKonOS 不自动提权）。</summary>
    Task<KillProcessResultDto> KillProcessAsync(int processId, bool force = false, CancellationToken ct = default);
}
