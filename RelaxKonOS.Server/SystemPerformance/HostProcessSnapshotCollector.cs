using System.Diagnostics;
using System.Security.Principal;
using RelaxKonOS.Protocol.SystemMonitor;
using RelaxKonOS.Server.SystemMonitor;

namespace RelaxKonOS.Server.SystemPerformance;

/// <summary>Used in the Server's ordinary context and in the Helper's privileged context.</summary>
internal static class HostProcessSnapshotCollector
{
    public static HostProcessSnapshotDto Capture(Func<int, bool>? visible = null)
    {
        var sampledAt = DateTimeOffset.UtcNow;
        var parentIds = ProcessTreeMetadata.CaptureParentIds();
        var userNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<HostProcessSampleDto>();
        Exception? firstFailure = null;
        var visibleCount = 0;
        var processes = Process.GetProcesses();
        try
        {
            if (processes.Length > 8192) throw new InvalidDataException("Host process count exceeds the snapshot limit.");
            foreach (var process in processes)
            {
                try
                {
                    if (visible is not null && !visible(process.Id)) continue;
                    visibleCount++;
                    var linux = OperatingSystem.IsLinux() ? LinuxProcessMetadata.ReadDetails(process.Id) : default;
                    var owner = linux.UserName;
                    if (OperatingSystem.IsWindows() && ProcessTreeMetadata.ReadWindowsOwnerSid(process.Id) is { } sid)
                    {
                        if (!userNames.TryGetValue(sid, out owner))
                        {
                            try { owner = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; }
                            catch { owner = sid; }
                            userNames[sid] = owner;
                        }
                    }
                    int? parent = OperatingSystem.IsLinux() ? ProcessTreeMetadata.ReadParentId(process)
                        : parentIds.TryGetValue(process.Id, out var parentId) ? parentId : null;
                    items.Add(new(new ProcessInfoDto(process.Id, Read(() => process.ProcessName, $"pid:{process.Id}"),
                        0, Read(() => process.WorkingSet64, 0L), owner,
                        Read<DateTimeOffset?>(() => new DateTimeOffset(process.StartTime.ToUniversalTime()), null),
                        OperatingSystem.IsLinux() ? linux.ThreadCount : Read(() => process.Threads.Count, 0), parent),
                        Read<long?>(() => process.TotalProcessorTime.Ticks, null)));
                }
                catch (Exception error) { firstFailure ??= error; }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        if (items.Count == 0 && (visibleCount > 0 || firstFailure is not null || OperatingSystem.IsWindows()))
            throw new InvalidOperationException($"Process enumeration produced no readable entries ({processes.Length} enumerated, {visibleCount} visible).",
                firstFailure);
        return new(items, sampledAt);
    }

    private static T Read<T>(Func<T> read, T unavailable)
    {
        try { return read(); }
        catch { return unavailable; }
    }
}
