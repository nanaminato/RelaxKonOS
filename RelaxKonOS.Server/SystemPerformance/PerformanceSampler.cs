using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.Server.SystemPerformance;

/// <summary>
/// 单例后台采样器。它是 CPU、磁盘和网络差分状态的唯一所有者，故订阅者和 HTTP 请求数量
/// 不会影响采样频率或速率计算。
/// </summary>
public sealed class PerformanceSampler(
    ISystemPerformanceSource source,
    PerformanceHistory history) : BackgroundService, IPerformanceSampler
{
    private readonly object _stateGate = new();
    private RawPerformanceSample? _previous;
    private long _sequence;
    private DateTimeOffset? _lastSuccess;
    private string? _lastError;

    public event Action<PerformanceRealtimeSnapshotDto>? SnapshotAvailable;

    public ValueTask<PerformanceInfoDto> GetInfoAsync(CancellationToken cancellationToken = default)
        => source.GetInfoAsync(cancellationToken);

    public PerformanceRealtimeSnapshotDto? GetLatest() => history.Latest();

    public IReadOnlyList<PerformanceRealtimeSnapshotDto> GetHistory(int seconds) => history.GetRecent(seconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        await SampleOnceAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await SampleOnceAsync(stoppingToken);
    }

    private async Task SampleOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var current = await source.ReadAsync(cancellationToken);
            RawPerformanceSample? previous;
            lock (_stateGate)
            {
                previous = _previous;
                _previous = current;
                _lastSuccess = current.Timestamp;
                _lastError = null;
            }

            // 第一份数据只建基线；不能把“尚未计算”伪装成 0% 或 0 B/s。
            if (previous is null) return;
            var snapshot = BuildSnapshot(previous, current);
            history.Add(snapshot);
            Publish(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 不向 API 暴露异常细节或宿主路径；下一周期继续尝试采集。
            lock (_stateGate) _lastError = "采样暂时不可用。";
        }
    }

    private PerformanceRealtimeSnapshotDto BuildSnapshot(RawPerformanceSample previous, RawPerformanceSample current)
    {
        var elapsedSeconds = Math.Max(0.001, (current.MonotonicTimestamp - previous.MonotonicTimestamp) / (double)Stopwatch.Frequency);
        var cpu = CreateCpu(previous.Cpu, current.Cpu);
        var filesystems = current.Filesystems.Select(file =>
        {
            var used = Math.Max(0, file.TotalBytes - file.AvailableBytes);
            return new FilesystemUsageDto(file.Id, file.TotalBytes, used, file.AvailableBytes,
                file.TotalBytes > 0 ? Math.Round(used * 100d / file.TotalBytes, 1) : 0);
        }).ToArray();
        var disks = CreateDisks(previous.Disks, current.Disks, elapsedSeconds);
        var networks = CreateNetworks(previous.Networks, current.Networks, elapsedSeconds);
        var memory = new MemoryRealtimeMetricsDto(
            current.Memory.TotalBytes,
            Math.Max(0, current.Memory.TotalBytes - current.Memory.AvailableBytes),
            current.Memory.AvailableBytes,
            current.Memory.CachedBytes,
            current.Memory.BufferedBytes,
            current.Memory.SwapTotalBytes is null || current.Memory.SwapAvailableBytes is null
                ? null : Math.Max(0, current.Memory.SwapTotalBytes.Value - current.Memory.SwapAvailableBytes.Value),
            current.Memory.SwapTotalBytes);
        DateTimeOffset? lastSuccess;
        string? error;
        lock (_stateGate) { lastSuccess = _lastSuccess; error = _lastError; }
        return new PerformanceRealtimeSnapshotDto(
            Interlocked.Increment(ref _sequence), current.Timestamp, cpu, memory, filesystems, disks, networks,
            current.UptimeSeconds, new PerformanceHealthDto(false, lastSuccess, error));
    }

    private static CpuRealtimeMetricsDto CreateCpu(RawCpuTimes previous, RawCpuTimes current)
    {
        static double? Percent(long? before, long? after, long deltaTotal)
            => before is null || after is null || deltaTotal <= 0 ? null : Math.Clamp((after.Value - before.Value) * 100d / deltaTotal, 0, 100);
        var total = current.Total - previous.Total;
        var usage = total <= 0 ? 0 : Math.Clamp((1 - (current.Idle - previous.Idle) / (double)total) * 100, 0, 100);
        var perCpu = new List<double>(current.LogicalProcessors.Count);
        for (var i = 0; i < current.LogicalProcessors.Count; i++)
        {
            if (i >= previous.LogicalProcessors.Count) continue;
            var now = current.LogicalProcessors[i];
            var before = previous.LogicalProcessors[i];
            var processorTotal = now.Total - before.Total;
            if (processorTotal <= 0) continue;
            perCpu.Add(Math.Round(Math.Clamp((1 - (now.Idle - before.Idle) / (double)processorTotal) * 100, 0, 100), 1));
        }
        return new CpuRealtimeMetricsDto(Math.Round(usage, 1),
            Percent(previous.User, current.User, total), Percent(previous.System, current.System, total),
            Percent(previous.Idle, current.Idle, total), Percent(previous.Iowait, current.Iowait, total), perCpu,
            current.CurrentFrequencyMHz, current.ProcessCount, current.ThreadCount, current.HandleCount);
    }

    private static IReadOnlyList<DiskRealtimeMetricsDto> CreateDisks(
        IReadOnlyList<RawDiskCounters> previous, IReadOnlyList<RawDiskCounters> current, double elapsed)
    {
        var prior = previous.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var result = new List<DiskRealtimeMetricsDto>();
        foreach (var now in current)
        {
            if (!prior.TryGetValue(now.Id, out var before)) continue;
            var readSectors = Delta(now.ReadSectors, before.ReadSectors);
            var writeSectors = Delta(now.WriteSectors, before.WriteSectors);
            var activity = Math.Clamp(Delta(now.BusyMilliseconds, before.BusyMilliseconds) / (elapsed * 10d), 0, 100);
            double? queue = now.QueueMilliseconds is null || before.QueueMilliseconds is null ? null
                : Math.Max(0, Delta(now.QueueMilliseconds.Value, before.QueueMilliseconds.Value) / (elapsed * 1000d));
            var completed = Delta(now.ReadOperations, before.ReadOperations) + Delta(now.WriteOperations, before.WriteOperations);
            double? latency = now.ReadMilliseconds is null || before.ReadMilliseconds is null
                || now.WriteMilliseconds is null || before.WriteMilliseconds is null || completed <= 0
                ? null
                : Math.Max(0, (Delta(now.ReadMilliseconds.Value, before.ReadMilliseconds.Value)
                    + Delta(now.WriteMilliseconds.Value, before.WriteMilliseconds.Value)) / (double)completed);
            result.Add(new DiskRealtimeMetricsDto(now.Id,
                (long)(readSectors * (double)now.SectorSizeBytes / elapsed),
                (long)(writeSectors * (double)now.SectorSizeBytes / elapsed),
                Math.Round(Delta(now.ReadOperations, before.ReadOperations) / elapsed, 1),
                Math.Round(Delta(now.WriteOperations, before.WriteOperations) / elapsed, 1),
                Math.Round(activity, 1), queue is null ? null : Math.Round(queue.Value, 2), latency is null ? null : Math.Round(latency.Value, 2)));
        }
        return result;
    }

    private static IReadOnlyList<NetworkRealtimeMetricsDto> CreateNetworks(
        IReadOnlyList<RawNetworkCounters> previous, IReadOnlyList<RawNetworkCounters> current, double elapsed)
    {
        var prior = previous.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var result = new List<NetworkRealtimeMetricsDto>();
        foreach (var now in current)
        {
            if (!prior.TryGetValue(now.Id, out var before)) continue;
            result.Add(new NetworkRealtimeMetricsDto(now.Id, now.BytesReceived, now.BytesSent,
                (long)(Delta(now.BytesReceived, before.BytesReceived) / elapsed),
                (long)(Delta(now.BytesSent, before.BytesSent) / elapsed), now.ReceivePackets, now.SendPackets,
                now.ReceiveErrors, now.SendErrors, now.ReceiveDropped, now.SendDropped));
        }
        return result;
    }

    private static long Delta(long current, long previous) => current >= previous ? current - previous : 0;

    private void Publish(PerformanceRealtimeSnapshotDto snapshot)
    {
        var handlers = SnapshotAvailable;
        if (handlers is null) return;
        foreach (Action<PerformanceRealtimeSnapshotDto> handler in handlers.GetInvocationList())
        {
            try { handler(snapshot); }
            catch { /* 订阅者不能影响采样循环。 */ }
        }
    }
}
