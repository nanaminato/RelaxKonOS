using System.Diagnostics;
using System.Net.NetworkInformation;
using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.Server.SystemMonitor;

/// <summary>系统指标采集基类：封装跨平台共享逻辑（进程列表/结束、磁盘空间、网络速率、GPU nvidia-smi、运行时间），
/// 仅将 CPU 与内存读取留给平台子类（Linux 读 /proc；Windows 走 P/Invoke）。Singleton：持有相邻采样差分状态。
/// 所有读取以宿主 OS 进程身份执行，复用宿主用户/权限（不另建 ACL）。</summary>
public abstract class SystemMetricsProviderBase : ISystemMetricsProvider
{
    private readonly object _gate = new();
    private Dictionary<int, (TimeSpan Cpu, DateTime At)> _procSamples = new();
    private Dictionary<string, (long Sent, long Recv, DateTime At)> _netSamples = new();

    public async Task<SystemMetricsDto> GetMetricsAsync(CancellationToken ct = default)
    {
        var cpu = await GetCpuUsageAsync(ct);
        var memory = await GetMemoryUsageAsync(ct);
        var disks = GetDiskUsage();
        var networks = GetNetworkUsage();
        var gpus = await GetGpuUsageAsync(ct);
        var uptime = (long)(Environment.TickCount64 / 1000);
        return new SystemMetricsDto(cpu, memory, disks, networks, gpus, uptime, DateTimeOffset.Now);
    }

    public Task<IReadOnlyList<ProcessInfoDto>> ListProcessesAsync(CancellationToken ct = default)
    {
        var cores = Environment.ProcessorCount;
        var now = DateTime.UtcNow;
        Dictionary<int, (TimeSpan Cpu, DateTime At)> prev;
        Dictionary<int, (TimeSpan Cpu, DateTime At)> next;
        lock (_gate)
        {
            prev = _procSamples;
            next = new Dictionary<int, (TimeSpan, DateTime)>();
        }

        var list = new List<ProcessInfoDto>();
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch { return Task.FromResult<IReadOnlyList<ProcessInfoDto>>(list); }

        foreach (var p in processes)
        {
            try
            {
                TimeSpan cpu;
                try { cpu = p.TotalProcessorTime; }
                catch { cpu = TimeSpan.Zero; }
                next[p.Id] = (cpu, now);

                double cpuPercent = 0;
                if (prev.TryGetValue(p.Id, out var s))
                {
                    var cpuDelta = (cpu - s.Cpu).TotalSeconds;
                    var elapsed = (now - s.At).TotalSeconds;
                    if (elapsed > 0)
                        cpuPercent = Math.Clamp(cpuDelta / (elapsed * cores) * 100, 0, 100);
                }

                long mem;
                try { mem = p.WorkingSet64; } catch { mem = 0; }

                int threads;
                try { threads = p.Threads.Count; } catch { threads = 0; }

                DateTimeOffset? start = null;
                try { start = new DateTimeOffset(p.StartTime.ToUniversalTime()); } catch { }

                string name;
                try { name = p.ProcessName; } catch { name = $"pid:{p.Id}"; }

                list.Add(new ProcessInfoDto(p.Id, name, Math.Round(cpuPercent, 1), mem, GetProcessUserName(p), start, threads));
            }
            catch { /* 单个进程读取失败跳过 */ }
            finally { try { p.Dispose(); } catch { } }
        }

        lock (_gate) { _procSamples = next; }

        // 默认按内存降序，CPU 占用高的在前易于定位（客户端可重排）
        list.Sort((a, b) => b.MemoryBytes.CompareTo(a.MemoryBytes));
        return Task.FromResult<IReadOnlyList<ProcessInfoDto>>(list);
    }

    public Task<KillProcessResultDto> KillProcessAsync(int processId, bool force = false, CancellationToken ct = default)
    {
        try
        {
            var p = Process.GetProcessById(processId);
            try
            {
                // Kill(entireProcessTree: false) — 仅终止目标进程，不波及子进程
                p.Kill(entireProcessTree: false);
                p.WaitForExit(3000);
                return Task.FromResult(new KillProcessResultDto(true, false, null));
            }
            finally { p.Dispose(); }
        }
        catch (ArgumentException)
        {
            return Task.FromResult(new KillProcessResultDto(false, false, $"进程不存在（id={processId}）。"));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 权限不足错误码：Windows ERROR_ACCESS_DENIED=5；Linux EPERM=1 / EACCES=13 → 需宿主 OS 提权
            var requiresElevation = ex.NativeErrorCode is 5 or 1 or 13;
            return Task.FromResult(new KillProcessResultDto(false, requiresElevation,
                requiresElevation ? $"权限不足，无法结束进程 {processId}（需在宿主 OS 提升权限，例如 sudo kill / UAC 运行）。" : ex.Message));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new KillProcessResultDto(false, false, ex.Message));
        }
    }

    // ── 平台特定：CPU 与内存 ──

    /// <summary>读取整机 CPU 占用。子类实现：Linux 读 /proc/stat；Windows 读 GetSystemTimes。</summary>
    protected abstract Task<CpuUsageDto> GetCpuUsageAsync(CancellationToken ct);

    /// <summary>读取内存占用。子类实现：Linux 读 /proc/meminfo；Windows 读 GlobalMemoryStatusEx。</summary>
    protected abstract Task<MemoryUsageDto> GetMemoryUsageAsync(CancellationToken ct);

    /// <summary>读取进程属主用户名（可空）。基类返回 null；Linux 子类解析 /proc uid→用户名。</summary>
    protected virtual string? GetProcessUserName(Process process) => null;

    // ── 跨平台共享：磁盘 / 网络 / GPU ──

    private static List<DiskUsageDto> GetDiskUsage()
    {
        var result = new List<DiskUsageDto>();
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); } catch { return result; }
        foreach (var d in drives)
        {
            try
            {
                if (!d.IsReady) continue;
                var total = d.TotalSize;
                var free = d.AvailableFreeSpace;
                if (total <= 0) continue;
                var used = total - free;
                var pct = total > 0 ? Math.Round((double)used / total * 100, 1) : 0;
                result.Add(new DiskUsageDto(d.Name, total, used, free, pct));
            }
            catch { /* 单个驱动器读取失败跳过 */ }
        }
        return result;
    }

    private List<NetworkUsageDto> GetNetworkUsage()
    {
        var now = DateTime.UtcNow;
        NetworkInterface[] ifaces;
        try { ifaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return new List<NetworkUsageDto>(); }

        var result = new List<NetworkUsageDto>();
        Dictionary<string, (long Sent, long Recv, DateTime At)> prev;
        var next = new Dictionary<string, (long Sent, long Recv, DateTime At)>();
        lock (_gate) { prev = _netSamples; }

        foreach (var ni in ifaces)
        {
            try
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var stats = ni.GetIPv4Statistics();
                long sent = stats.BytesSent;
                long recv = stats.BytesReceived;
                next[ni.Name] = (sent, recv, now);

                long sendRate = 0, recvRate = 0;
                if (prev.TryGetValue(ni.Name, out var s))
                {
                    var elapsed = (now - s.At).TotalSeconds;
                    if (elapsed > 0)
                    {
                        // 计数器可能因接口重置而回绕/归零，做下界保护
                        sendRate = sent >= s.Sent ? (long)((sent - s.Sent) / elapsed) : 0;
                        recvRate = recv >= s.Recv ? (long)((recv - s.Recv) / elapsed) : 0;
                    }
                }
                result.Add(new NetworkUsageDto(ni.Name, sent, recv, sendRate, recvRate));
            }
            catch { /* 单个接口读取失败跳过 */ }
        }

        lock (_gate) { _netSamples = next; }
        return result;
    }

    /// <summary>GPU 占用（best-effort）：通过 nvidia-smi 解析。Linux/Windows 通用；非 NVIDIA 或无驱动返回空列表。</summary>
    private static async Task<IReadOnlyList<GpuUsageDto>> GetGpuUsageAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "nvidia-smi.exe" : "nvidia-smi",
                Arguments = "--query-gpu=name,utilization.gpu,memory.total,memory.used,temperature.gpu --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return Array.Empty<GpuUsageDto>();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            if (!proc.WaitForExit(3000)) { try { proc.Kill(); } catch { } return Array.Empty<GpuUsageDto>(); }
            var stdout = await stdoutTask;

            var list = new List<GpuUsageDto>();
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',');
                if (parts.Length < 5) continue;
                var name = parts[0].Trim();
                double? usage = double.TryParse(parts[1].Trim(), out var u) ? u : null;
                // memory.total / memory.used 单位为 MiB，转换为字节
                long? memTotal = long.TryParse(parts[2].Trim(), out var mt) ? mt * 1024 * 1024 : null;
                long? memUsed = long.TryParse(parts[3].Trim(), out var mu) ? mu * 1024 * 1024 : null;
                double? temp = double.TryParse(parts[4].Trim(), out var t) ? t : null;
                list.Add(new GpuUsageDto(name, usage, memTotal, memUsed, temp));
            }
            return list;
        }
        catch
        {
            return Array.Empty<GpuUsageDto>();
        }
    }
}
