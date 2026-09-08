using System.Diagnostics;

namespace RelaxKonOS.Server.SystemPerformance;

/// <summary>供性能采样器显示的系统级进程摘要；单个受保护进程不可读时仍保留其他进程的统计。</summary>
internal static class SystemProcessSummary
{
    public static ProcessSummary Read()
    {
        var processes = 0;
        var threads = 0;
        long handles = 0;
        var hasHandleCount = OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

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
            if (OperatingSystem.IsLinux()) return Directory.EnumerateFileSystemEntries($"/proc/{process.Id}/fd").Count();
        }
        catch { }
        return 0;
    }
}

internal readonly record struct ProcessSummary(int? ProcessCount, int? ThreadCount, long? HandleCount);
