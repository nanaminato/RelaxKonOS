using System.Diagnostics;
using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.Server.SystemPerformance;

/// <summary>
/// 按需的低频进程采样器。只有进程页查询才会枚举进程；PID 与 StartTime 共同作为实例身份，
/// 避免 PID 重用继承旧进程 CPU 时间。
/// </summary>
public sealed class ProcessSampler(RelaxKonOS.Server.SystemMonitor.ISystemMetricsProvider legacyControl) : IProcessService
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _collectionGate = new(1, 1);
    private static readonly TimeSpan SamplingInterval = TimeSpan.FromSeconds(5);
    private Dictionary<ProcessInstanceKey, ProcessSample> _previous = new();
    private ProcessPageDto _latest = new([], 0, DateTimeOffset.MinValue);

    public async Task<ProcessPageDto> QueryAsync(int page, int pageSize, string? filter, string? sort, bool descending,
        CancellationToken cancellationToken = default)
    {
        await EnsureFreshAsync(cancellationToken);
        ProcessPageDto snapshot;
        lock (_gate) snapshot = _latest;
        IEnumerable<ProcessInfoDto> query = snapshot.Items;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var text = filter.Trim();
            query = query.Where(p => p.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                || p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(text, StringComparison.OrdinalIgnoreCase)
                || (p.UserName?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        query = (sort?.ToLowerInvariant()) switch
        {
            "cpu" => descending ? query.OrderByDescending(x => x.CpuPercent) : query.OrderBy(x => x.CpuPercent),
            "name" => descending ? query.OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase) : query.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            "pid" => descending ? query.OrderByDescending(x => x.Id) : query.OrderBy(x => x.Id),
            _ => descending ? query.OrderByDescending(x => x.MemoryBytes) : query.OrderBy(x => x.MemoryBytes),
        };
        var all = query.ToArray();
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 500);
        return new ProcessPageDto(all.Skip((safePage - 1) * safePageSize).Take(safePageSize).ToArray(), all.Length, snapshot.SampledAt);
    }

    public Task<KillProcessResultDto> KillAsync(int processId, bool force, CancellationToken cancellationToken = default)
        => legacyControl.KillProcessAsync(processId, force, cancellationToken);

    private async Task EnsureFreshAsync(CancellationToken cancellationToken)
    {
        ProcessPageDto latest;
        lock (_gate) latest = _latest;
        if (DateTimeOffset.UtcNow - latest.SampledAt < SamplingInterval) return;

        await _collectionGate.WaitAsync(cancellationToken);
        try
        {
            lock (_gate) latest = _latest;
            var age = DateTimeOffset.UtcNow - latest.SampledAt;
            if (age >= SamplingInterval)
            {
                // A dormant process page has no useful adjacent baseline. Start a fresh one instead
                // of reporting a long-term average as if it were the current CPU percentage.
                if (age > SamplingInterval + SamplingInterval)
                    lock (_gate) _previous = new();
                await CollectAsync(cancellationToken);
            }
        }
        finally { _collectionGate.Release(); }
    }

    private Task CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        Dictionary<ProcessInstanceKey, ProcessSample> previous;
        lock (_gate) previous = _previous;
        var next = new Dictionary<ProcessInstanceKey, ProcessSample>();
        var processes = new List<ProcessInfoDto>();
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var startTime = TryGetStartTime(process);
                    var key = new ProcessInstanceKey(process.Id, startTime);
                    var linuxDetails = OperatingSystem.IsLinux()
                        ? RelaxKonOS.Server.SystemMonitor.LinuxProcessMetadata.ReadDetails(process.Id)
                        : new RelaxKonOS.Server.SystemMonitor.LinuxProcessDetails(null, 0);
                    var cpu = TryGetCpu(process);
                    next[key] = new ProcessSample(cpu, now);
                    var cpuPercent = 0d;
                    if (previous.TryGetValue(key, out var prior))
                    {
                        var elapsed = (now - prior.At).TotalSeconds;
                        if (elapsed > 0)
                            cpuPercent = Math.Clamp((cpu - prior.Cpu).TotalSeconds / (elapsed * Environment.ProcessorCount) * 100, 0, 100);
                    }
                    processes.Add(new ProcessInfoDto(process.Id, TryGetName(process), Math.Round(cpuPercent, 1), TryGetMemory(process),
                        linuxDetails.UserName, startTime, TryGetThreadCount(process, linuxDetails)));
                }
                catch { /* a terminated/protected single process cannot break the whole snapshot */ }
                finally { process.Dispose(); }
            }
        }
        catch { /* Process.GetProcesses itself can fail under restricted hosts */ }
        processes.Sort((left, right) => right.MemoryBytes.CompareTo(left.MemoryBytes));
        lock (_gate)
        {
            _previous = next;
            _latest = new ProcessPageDto(processes, processes.Count, now);
        }
        return Task.CompletedTask;
    }

    private static DateTimeOffset? TryGetStartTime(Process process)
    {
        try { return new DateTimeOffset(process.StartTime.ToUniversalTime()); }
        catch { return null; }
    }

    private static TimeSpan TryGetCpu(Process process) { try { return process.TotalProcessorTime; } catch { return TimeSpan.Zero; } }
    private static long TryGetMemory(Process process) { try { return process.WorkingSet64; } catch { return 0; } }
    private static int TryGetThreadCount(Process process, RelaxKonOS.Server.SystemMonitor.LinuxProcessDetails linuxDetails)
        => OperatingSystem.IsLinux()
            ? linuxDetails.ThreadCount
            : TryGetWindowsThreadCount(process);

    private static int TryGetWindowsThreadCount(Process process) { try { return process.Threads.Count; } catch { return 0; } }
    private static string TryGetName(Process process) { try { return process.ProcessName; } catch { return $"pid:{process.Id}"; } }

    private readonly record struct ProcessInstanceKey(int Id, DateTimeOffset? StartTime);
    private readonly record struct ProcessSample(TimeSpan Cpu, DateTimeOffset At);
}
