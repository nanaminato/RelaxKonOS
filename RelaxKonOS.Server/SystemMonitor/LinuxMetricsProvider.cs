using System.Diagnostics;
using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.Server.SystemMonitor;

/// <summary>Linux（Ubuntu）系统指标采集。CPU/内存读 /proc（stat/meminfo），进程属主读 /proc/[pid]/status + /etc/passwd。
/// 单例持有 /proc/stat 相邻采样以差分计算 CPU%。所有读取以服务端进程身份执行（复用宿主 OS 用户/权限）。</summary>
public sealed class LinuxMetricsProvider : SystemMetricsProviderBase
{
    private readonly object _cpuGate = new();
    private Dictionary<string, (long Total, long Idle, DateTime At)> _cpuPrev = new();

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
            return LinuxProcessMetadata.GetUserName(process.Id);
        }
        catch { return null; }
    }

    private static long ParseKb(string line)
    {
        var span = line.AsSpan();
        var colon = span.IndexOf(':');
        if (colon < 0) return 0;
        var fields = span[(colon + 1)..].Trim();
        var separator = fields.IndexOfAny(' ', '\t');
        var value = separator < 0 ? fields : fields[..separator];
        return long.TryParse(value, out var kib) ? kib : 0;
    }
}

/// <summary>Shared Linux process metadata reader. The passwd map is loaded once, while volatile /proc data stays per-process.</summary>
internal static class LinuxProcessMetadata
{
    private static readonly Lazy<IReadOnlyDictionary<uint, string>> UserNames = new(LoadPasswd, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string? GetUserName(int pid)
    {
        try
        {
            return ReadDetails(pid).UserName;
        }
        catch { return null; }
    }

    public static int CountThreads(int pid)
    {
        try { return ReadStatus(pid).ThreadCount; }
        catch { return 0; }
    }

    public static LinuxProcessDetails ReadDetails(int pid)
    {
        try
        {
            var status = ReadStatus(pid);
            if (status.Uid is null) return new(null, status.ThreadCount);
            var userName = UserNames.Value.TryGetValue(status.Uid.Value, out var name) ? name : status.Uid.Value.ToString();
            return new(userName, status.ThreadCount);
        }
        catch { return new(null, 0); }
    }

    private static LinuxProcessStatus ReadStatus(int pid)
    {
        uint? uid = null;
        var threads = 0;
        foreach (var line in File.ReadLines($"/proc/{pid}/status"))
        {
            if (line.StartsWith("Uid:", StringComparison.Ordinal))
                uid = ParseFirstUInt(line.AsSpan(4));
            else if (line.StartsWith("Threads:", StringComparison.Ordinal))
            {
                var count = ParseFirstUInt(line.AsSpan(8));
                threads = count is { } value && value <= (uint)int.MaxValue ? (int)value : 0;
            }
            if (uid is not null && threads > 0) break;
        }
        return new(uid, threads);
    }

    private static uint? ParseFirstUInt(ReadOnlySpan<char> fields)
    {
        fields = fields.Trim();
        var separator = fields.IndexOfAny(' ', '\t');
        var value = separator < 0 ? fields : fields[..separator];
        return uint.TryParse(value, out var parsed) ? parsed : null;
    }

    private static IReadOnlyDictionary<uint, string> LoadPasswd()
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

    private readonly record struct LinuxProcessStatus(uint? Uid, int ThreadCount);
}

internal readonly record struct LinuxProcessDetails(string? UserName, int ThreadCount);
