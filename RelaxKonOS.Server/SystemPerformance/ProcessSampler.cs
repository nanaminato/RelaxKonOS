using RelaxKonOS.Protocol.SystemMonitor;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.SystemPerformance;

/// <summary>Ordinary and privileged snapshots have separate caches and CPU baselines.
/// Request authorization is checked before this singleton is called, including cached reads.</summary>
public sealed class ProcessSampler(
    RelaxKonOS.Server.SystemMonitor.ISystemMetricsProvider legacyControl,
    IServerModeResolver mode,
    IPrivilegedOperationTransport privilegedOperations) : IProcessService
{
    private static readonly TimeSpan SamplingInterval = TimeSpan.FromSeconds(5);
    private readonly SampleCache _ordinary = new();
    private readonly SampleCache _elevated = new();
    private readonly ProcessIconProvider _icons = new();

    public async Task<ProcessPageDto> QueryAsync(int page, int pageSize, string? filter, string? sort, bool descending,
        bool elevated = false, CancellationToken cancellationToken = default)
    {
        var cache = elevated ? _elevated : _ordinary;
        await EnsureFreshAsync(cache, elevated, cancellationToken);
        ProcessPageDto snapshot;
        lock (cache.Gate) snapshot = cache.Latest;
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
        return new(all.Skip((int)Math.Min((long)(safePage - 1) * safePageSize, int.MaxValue)).Take(safePageSize).ToArray(),
            all.Length, snapshot.SampledAt);
    }

    public async Task<KillProcessResultDto> KillAsync(int processId, DateTimeOffset expectedStartTime,
        bool elevated = false, CancellationToken cancellationToken = default)
    {
        if (processId <= 0 || expectedStartTime == default) return new(false, false, "process.invalid_instance", null);
        KillProcessResultDto result;
        if (elevated)
        {
            var receipt = await privilegedOperations.ExecuteAsync(new(PrivilegedOperationKind.ProcessTerminate,
                ProcessId: processId, ProcessExpectedStartTime: expectedStartTime), cancellationToken);
            result = receipt.ProcessTermination ?? new(false, false, "process.operation_failed",
                $"Process termination failed: {receipt.ProblemCode}.");
        }
        else
        {
            if (!IsVisibleOrdinarily(processId)) return new(false, false, "process.user_mode_not_owned", null);
            result = await legacyControl.KillProcessAsync(processId, expectedStartTime, cancellationToken);
        }
        lock (_ordinary.Gate) _ordinary.Latest = _ordinary.Latest with { SampledAt = DateTimeOffset.MinValue };
        lock (_elevated.Gate) _elevated.Latest = _elevated.Latest with { SampledAt = DateTimeOffset.MinValue };
        return result;
    }

    public async Task<ProcessIconContent?> GetIconAsync(int processId, DateTimeOffset expectedStartTime,
        bool elevated = false, CancellationToken cancellationToken = default)
    {
        if (processId <= 0 || expectedStartTime == default) return null;
        if (!elevated)
            return IsVisibleOrdinarily(processId) ? await _icons.GetIconAsync(processId, expectedStartTime, cancellationToken) : null;
        var result = await privilegedOperations.ExecuteAsync(new(PrivilegedOperationKind.ProcessIcon,
            ProcessId: processId, ProcessExpectedStartTime: expectedStartTime), cancellationToken);
        if (!result.Success || result.OutputBase64 is not { Length: > 0 } encoded
            || result.ProcessIconContentType is not ("image/png" or "image/svg+xml")) return null;
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            return bytes.Length is > 0 and <= 262_144 ? new(bytes, result.ProcessIconContentType) : null;
        }
        catch (FormatException) { return null; }
    }

    private async Task EnsureFreshAsync(SampleCache cache, bool elevated, CancellationToken ct)
    {
        lock (cache.Gate) if (DateTimeOffset.UtcNow - cache.Latest.SampledAt < SamplingInterval) return;
        await cache.CollectionGate.WaitAsync(ct);
        try
        {
            lock (cache.Gate) if (DateTimeOffset.UtcNow - cache.Latest.SampledAt < SamplingInterval) return;
            HostProcessSnapshotDto raw;
            if (elevated)
            {
                var result = await privilegedOperations.ExecuteAsync(new(PrivilegedOperationKind.ProcessSnapshot), ct);
                ct.ThrowIfCancellationRequested();
                if (!result.Success || result.ProcessSnapshot is not { Items: { Count: <= 8192 } } snapshot
                    || snapshot.SampledAt == DateTimeOffset.MinValue
                    || snapshot.Items.Any(item => item is null || item.Process is null || item.Process.Id < 0 || item.CpuTicks < 0)
                    || snapshot.Items.Select(item => item.Process.Id).Distinct().Count() != snapshot.Items.Count)
                    throw new ProcessSnapshotUnavailableException($"Process snapshot failed: {(result.Success ? PrivilegedProblemCode.InvalidProtocol : result.ProblemCode)}. {result.Error}");
                raw = snapshot;
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                try { raw = HostProcessSnapshotCollector.Capture(IsVisibleOrdinarily); }
                catch (Exception error) { throw new ProcessSnapshotUnavailableException($"Process snapshot failed: {error.Message}"); }
            }
            var next = new Dictionary<ProcessInstanceKey, ProcessSample>();
            var items = new List<ProcessInfoDto>(raw.Items.Count);
            foreach (var item in raw.Items)
            {
                var key = new ProcessInstanceKey(item.Process.Id, item.Process.StartTime);
                var cpuPercent = 0d;
                if (key.StartTime is not null && item.CpuTicks is { } cpu && cache.Previous.TryGetValue(key, out var prior)
                    && prior.CpuTicks is { } priorCpu)
                {
                    var elapsed = (raw.SampledAt - prior.At).TotalSeconds;
                    if (elapsed is > 0 and <= 10)
                        cpuPercent = Math.Clamp((cpu - priorCpu) / (double)TimeSpan.TicksPerSecond / (elapsed * Environment.ProcessorCount) * 100, 0, 100);
                }
                next[key] = new(item.CpuTicks, raw.SampledAt);
                items.Add(item.Process with { CpuPercent = Math.Round(cpuPercent, 1) });
            }
            lock (cache.Gate) { cache.Previous = next; cache.Latest = new(items, items.Count, raw.SampledAt); }
        }
        finally { cache.CollectionGate.Release(); }
    }

    private bool IsVisibleOrdinarily(int pid)
    {
        if (mode.Mode != ServerMode.User || OperatingSystem.IsWindows()) return true;
        if (!OperatingSystem.IsLinux()) return false;
        var uid = RelaxKonOS.Server.SystemMonitor.LinuxProcessMetadata.ReadUid(pid);
        var owner = mode.Describe().ExecutionIdentity.Uid;
        return uid is { } actual && owner >= 0 && actual == (uint)owner;
    }

    private sealed class SampleCache
    {
        public object Gate { get; } = new();
        public SemaphoreSlim CollectionGate { get; } = new(1, 1);
        public Dictionary<ProcessInstanceKey, ProcessSample> Previous { get; set; } = new();
        public ProcessPageDto Latest { get; set; } = new([], 0, DateTimeOffset.MinValue);
    }
    private readonly record struct ProcessInstanceKey(int Id, DateTimeOffset? StartTime);
    private readonly record struct ProcessSample(long? CpuTicks, DateTimeOffset At);
}

public sealed class ProcessSnapshotUnavailableException(string message) : InvalidOperationException(message);
