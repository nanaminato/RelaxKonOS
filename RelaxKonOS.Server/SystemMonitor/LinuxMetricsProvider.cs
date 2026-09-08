using System.Diagnostics;
using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.Server.SystemMonitor;

/// <summary>Linux（Ubuntu）系统指标采集。CPU/内存读 /proc（stat/meminfo），进程属主读 /proc/[pid]/status + /etc/passwd。
/// 单例持有 /proc/stat 相邻采样以差分计算 CPU%。所有读取以服务端进程身份执行（复用宿主 OS 用户/权限）。</summary>
public sealed class LinuxMetricsProvider : SystemMetricsProviderBase
{
    private readonly object _cpuGate = new();
    private Dictionary<string, (long Total, long Idle, DateTime At)> _cpuPrev = new();
    private Dictionary<uint, string>? _uidMap;

    protected override Task<CpuUsageDto> GetCpuUsageAsync(CancellationToken ct)
    {
        // /proc/stat 首行聚合：cpu  user nice system idle iowait irq softirq steal ...
        // 各值单位为 USER_HZ（jiffies，通常 100Hz）。idle_all = idle + iowait。
        // usage% = (1 - idle_delta/total_delta) * 100。cpu0..cpuN-1 为每核行。
        Dictionary<string, (long Total, long Idle, DateTime At)> prev;
        var next = new Dictionary<string, (long Total, long Idle, DateTime At)>();
        lock (_cpuGate) prev = _cpuPrev;

        var now = DateTime.UtcNow;
        var perCore = new List<double>();
        double totalPercent = 0;
        try
        {
            foreach (var rawLine in File.ReadLines("/proc/stat"))
            {
                if (!rawLine.StartsWith("cpu", StringComparison.Ordinal)) continue;
                var tag = rawLine[..rawLine.IndexOf(' ')];       // "cpu" 或 "cpu0"...
                var isAggregate = tag == "cpu";
                // 仅取聚合 + 逻辑核心行；/proc/stat 不含超线程以外的虚拟行，cpuN 即逻辑核
                var fields = rawLine.AsSpan().Slice(rawLine.IndexOf(' ')).ToString()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                long total = 0, idle = 0;
                for (int i = 0; i < fields.Length; i++)
                {
                    if (!long.TryParse(fields[i], out var v)) continue;
                    total += v;
                    // 第 4 列(index 3)=idle，第 5 列(index 4)=iowait
                    if (i == 3 || i == 4) idle += v;
                }
                next[tag] = (total, idle, now);

                double pct = 0;
                if (prev.TryGetValue(tag, out var s))
                {
                    var dTotal = total - s.Total;
                    var dIdle = idle - s.Idle;
                    if (dTotal > 0) pct = Math.Clamp((1 - (double)dIdle / dTotal) * 100, 0, 100);
                }
                if (isAggregate) totalPercent = Math.Round(pct, 1);
                else perCore.Add(Math.Round(pct, 1));
            }
        }
        catch
        {
            // /proc 不可读（非 Linux 或权限问题）——回退 0
        }

        lock (_cpuGate) _cpuPrev = next;

        var coreCount = perCore.Count > 0 ? perCore.Count : Environment.ProcessorCount;
        if (perCore.Count == 0) perCore.AddRange(Enumerable.Repeat(0.0, coreCount));
        return Task.FromResult(new CpuUsageDto(totalPercent, perCore, coreCount));
    }

    protected override Task<MemoryUsageDto> GetMemoryUsageAsync(CancellationToken ct)
    {
        long totalBytes = 0, availableBytes = 0;
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                // MemTotal:  16384000 kB
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                    totalBytes = ParseKb(line) * 1024;
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    availableBytes = ParseKb(line) * 1024;
                if (totalBytes > 0 && availableBytes > 0) break;
            }
        }
        catch { /* 非 Linux 回退 0 */ }

        var used = Math.Max(0, totalBytes - availableBytes);
        var pct = totalBytes > 0 ? Math.Round((double)used / totalBytes * 100, 1) : 0;
        return Task.FromResult(new MemoryUsageDto(totalBytes, used, availableBytes, pct));
    }

    protected override string? GetProcessUserName(Process process)
    {
        try
        {
            var uid = ReadUidFromStatus(process.Id);
            if (uid is null) return null;
            return ResolveUserName(uid.Value);
        }
        catch { return null; }
    }

    private static uint? ReadUidFromStatus(int pid)
    {
        foreach (var line in File.ReadLines($"/proc/{pid}/status"))
        {
            if (!line.StartsWith("Uid:", StringComparison.Ordinal)) continue;
            // Uid: real effective saved fs
            var parts = line.AsSpan().Slice(4).ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && uint.TryParse(parts[0], out var uid)) return uid;
            break;
        }
        return null;
    }

    private string ResolveUserName(uint uid)
    {
        var map = _uidMap ??= LoadPasswd();
        return map.TryGetValue(uid, out var name) ? name : uid.ToString();
    }

    private static Dictionary<uint, string> LoadPasswd()
    {
        var map = new Dictionary<uint, string>();
        try
        {
            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                // name:x:uid:gid:gecos:home:shell
                var parts = line.Split(':');
                if (parts.Length >= 3 && uint.TryParse(parts[2], out var uid))
                    map[uid] = parts[0];
            }
        }
        catch { /* /etc/passwd 不可读——退化为 uid 数字 */ }
        return map;
    }

    private static long ParseKb(string line)
    {
        var span = line.AsSpan();
        var colon = span.IndexOf(':');
        if (colon < 0) return 0;
        var rest = span.Slice(colon + 1).ToString().Trim();
        // 形如 "16384000 kB"
        var space = rest.IndexOf(' ');
        var num = space >= 0 ? rest[..space] : rest;
        return long.TryParse(num, out var v) ? v : 0;
    }
}
