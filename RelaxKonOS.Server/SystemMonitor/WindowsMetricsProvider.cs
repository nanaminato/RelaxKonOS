using System.Diagnostics;
using System.Runtime.InteropServices;
using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.Server.SystemMonitor;

/// <summary>Windows（Windows Server）系统指标采集。CPU 经 GetSystemTimes 差分（kernel32），
/// 内存经 GlobalMemoryStatusEx。单例持相邻采样以差分。仅在 Windows 平台注册。
/// 当前限制：per-core 暂以整机占比填充（GetSystemTimes 仅返回聚合；逐核需 NtQuerySystemInformation，留待后续）。</summary>
public sealed class WindowsMetricsProvider : SystemMetricsProviderBase
{
    private readonly object _cpuGate = new();
    private (ulong Idle, ulong Total, DateTime At) _cpuPrev;

    protected override Task<CpuUsageDto> GetCpuUsageAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        double totalPercent = 0;
        if (NativeMethods.GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
        {
            var idle = ToUlong(in idleFt);
            // kernel time 已含 idle time；total = kernel + user
            var total = ToUlong(in kernelFt) + ToUlong(in userFt);

            lock (_cpuGate)
            {
                var prev = _cpuPrev;
                if (prev.At != default && total > prev.Total)
                {
                    var dTotal = total - prev.Total;
                    var dIdle = idle - prev.Idle;
                    if (dTotal > 0)
                        totalPercent = Math.Clamp((1 - (double)dIdle / dTotal) * 100, 0, 100);
                }
                _cpuPrev = (idle, total, now);
            }
        }

        var coreCount = Environment.ProcessorCount;
        // 当前：逐核暂以整机占比填充
        var perCore = Enumerable.Repeat(Math.Round(totalPercent, 1), coreCount).ToList();
        return Task.FromResult(new CpuUsageDto(Math.Round(totalPercent, 1), perCore, coreCount));
    }

    protected override Task<MemoryUsageDto> GetMemoryUsageAsync(CancellationToken ct)
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        long totalBytes = 0, availableBytes = 0, usedBytes = 0;
        double pct = 0;
        if (NativeMethods.GlobalMemoryStatusEx(ref status))
        {
            totalBytes = (long)status.ullTotalPhys;
            availableBytes = (long)status.ullAvailPhys;
            usedBytes = Math.Max(0, totalBytes - availableBytes);
            pct = Math.Round((double)status.dwMemoryLoad, 1);
        }
        return Task.FromResult(new MemoryUsageDto(totalBytes, usedBytes, availableBytes, pct));
    }

    private static ulong ToUlong(in FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
