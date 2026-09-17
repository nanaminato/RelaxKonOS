using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace RelaxKonOS.Server.SystemPerformance;

/// <summary>供性能采样器显示的系统级进程摘要；单个受保护进程不可读时仍保留其他进程的统计。</summary>
internal static class SystemProcessSummary
{
    private static readonly object CacheGate = new();
    private static ProcessSummary? _cached;
    private static long _nextRefreshTimestamp;

    public static ProcessSummary Read()
    {
        var now = Stopwatch.GetTimestamp();
        lock (CacheGate)
        {
            if (_cached is { } cached && now < _nextRefreshTimestamp) return cached;
            var summary = OperatingSystem.IsLinux() ? ReadLinux() : ReadWindows();
            _cached = summary;
            _nextRefreshTimestamp = now + Stopwatch.Frequency * 5;
            return summary;
        }
    }

    private static ProcessSummary ReadLinux()
    {
        var processes = 0;
        var threads = 0;
        long handles = 0;
        try
        {
            foreach (var path in Directory.EnumerateDirectories("/proc"))
            {
                var name = Path.GetFileName(path);
                if (!int.TryParse(name, out var pid)) continue;
                processes++;
                threads += RelaxKonOS.Server.SystemMonitor.LinuxProcessMetadata.CountThreads(pid);
                handles += CountFileEntries(Path.Combine(path, "fd"));
            }
        }
        catch { return new ProcessSummary(null, null, null); }
        return new ProcessSummary(processes, threads, handles);
    }

    private static int CountFileEntries(string path)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(path).Count();
        }
        catch { return 0; }
    }

    private static ProcessSummary ReadWindows()
    {
        var processes = 0;
        var threads = 0;
        long handles = 0;
        const bool hasHandleCount = true;

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    processes++;
                    threads += TryGetThreadCount(process);
                    if (hasHandleCount) handles += TryGetHandleCount(process);
                }
                catch { /* A protected or exiting process is excluded from its unavailable fields. */ }
                finally { process.Dispose(); }
            }
        }
        catch { return new ProcessSummary(null, null, null); }

        return new ProcessSummary(processes, threads, hasHandleCount ? handles : null);
    }

    private static int TryGetThreadCount(Process process) { try { return process.Threads.Count; } catch { return 0; } }

    private static int TryGetHandleCount(Process process)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return process.HandleCount;
        }
        catch { }
        return 0;
    }
}

internal readonly record struct ProcessSummary(int? ProcessCount, int? ThreadCount, long? HandleCount);
