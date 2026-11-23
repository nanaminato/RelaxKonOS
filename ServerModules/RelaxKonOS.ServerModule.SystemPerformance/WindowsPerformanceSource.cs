using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.Server.SystemPerformance;

/// <summary>Windows 原始性能数据源。仅封装 Win32/NT 查询；不持有任何差分状态。</summary>
public sealed class WindowsPerformanceSource : ISystemPerformanceSource
{
    public ValueTask<PerformanceInfoDto> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var memory = ReadMemory();
        var filesystems = ReadFilesystemInfo();
        var disks = ReadDiskCounters();
        var filesystemIdsByDisk = ReadDiskFilesystemIds();
        return ValueTask.FromResult(new PerformanceInfoDto(
            new CpuInfoDto(null, null, Environment.ProcessorCount, null, null),
            new MemoryInfoDto(memory.TotalBytes, memory.SwapTotalBytes),
            filesystems, disks.Select(x => new DiskInfoDto(x.Id, x.Id.Replace("windows-disk:", "PhysicalDrive", StringComparison.Ordinal), null,
                int.TryParse(x.Id.AsSpan("windows-disk:".Length), out var number) && filesystemIdsByDisk.TryGetValue(number, out var ids) ? ids : [])).ToArray(), ReadNetworkInfo(),
            new PerformanceCapabilitiesDto(true, false, false, disks.Count > 0, disks.Count > 0, false, true, false)));
    }

    public ValueTask<RawPerformanceSample> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new RawPerformanceSample(DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), ReadCpu(), ReadMemory(),
            ReadFilesystemUsage(), ReadDiskCounters(), ReadNetworkCounters(), Environment.TickCount64 / 1000));
    }

    private static RawCpuTimes ReadCpu()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            return new RawCpuTimes(0, 0, null, null, null, Array.Empty<RawCpuTimes>(), null);
        var idleTicks = ToLong(idle);
        var kernelTicks = ToLong(kernel);
        var userTicks = ToLong(user);
        var logical = ReadLogicalCpuTimes();
        var processSummary = SystemProcessSummary.Read();
        return new RawCpuTimes(kernelTicks + userTicks, idleTicks, userTicks, Math.Max(0, kernelTicks - idleTicks), null, logical, null,
            processSummary.ProcessCount, processSummary.ThreadCount, processSummary.HandleCount);
    }

    private static IReadOnlyList<RawCpuTimes> ReadLogicalCpuTimes()
    {
        var elementSize = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
        var size = checked(elementSize * Math.Max(1, Environment.ProcessorCount));
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var status = NativeMethods.NtQuerySystemInformation(NativeMethods.SystemProcessorPerformanceInformation, buffer, size, out var returned);
            if (status != 0 || returned < elementSize) return Array.Empty<RawCpuTimes>();
            var count = Math.Min(Environment.ProcessorCount, returned / elementSize);
            var result = new List<RawCpuTimes>(count);
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(buffer + i * elementSize);
                var kernel = Math.Max(0, info.KernelTime);
                var user = Math.Max(0, info.UserTime);
                var idle = Math.Max(0, info.IdleTime);
                result.Add(new RawCpuTimes(kernel + user, idle, user, Math.Max(0, kernel - idle), null, Array.Empty<RawCpuTimes>(), null));
            }
            return result;
        }
        catch { return Array.Empty<RawCpuTimes>(); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static RawMemory ReadMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref status)) return new RawMemory(0, 0, null, null, null, null);
        var total = ToLong(status.ullTotalPhys);
        var available = ToLong(status.ullAvailPhys);
        var pageTotal = ToLong(status.ullTotalPageFile);
        var pageAvailable = ToLong(status.ullAvailPageFile);
        return new RawMemory(total, available, null, null, pageTotal, pageAvailable);
    }

    private static IReadOnlyList<FilesystemInfoDto> ReadFilesystemInfo()
    {
        var result = new List<FilesystemInfoDto>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.IsReady) result.Add(new FilesystemInfoDto(FilesystemId(drive), drive.Name, drive.RootDirectory.FullName));
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    private static IReadOnlyList<RawFilesystemUsage> ReadFilesystemUsage()
    {
        var result = new List<RawFilesystemUsage>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.IsReady && drive.TotalSize > 0)
                        result.Add(new RawFilesystemUsage(FilesystemId(drive), drive.TotalSize, drive.AvailableFreeSpace));
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Resolves Windows volumes to their backing physical disks. A volume may span multiple disks,
    /// so every extent is preserved rather than choosing an arbitrary first disk.
    /// </summary>
    private static IReadOnlyDictionary<int, IReadOnlyList<string>> ReadDiskFilesystemIds()
    {
        var result = new Dictionary<int, List<string>>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    var volume = drive.RootDirectory.FullName.TrimEnd('\\');
                    if (volume.Length != 2 || volume[1] != ':') continue;
                    var handle = NativeMethods.CreateFile($@"\\.\{volume}", 0,
                        NativeMethods.FileShareRead | NativeMethods.FileShareWrite, IntPtr.Zero, NativeMethods.OpenExisting, 0, IntPtr.Zero);
                    if (handle == NativeMethods.InvalidHandleValue) continue;
                    try
                    {
                        const int bufferSize = 512;
                        var buffer = Marshal.AllocHGlobal(bufferSize);
                        try
                        {
                            if (!NativeMethods.DeviceIoControlRaw(handle, NativeMethods.IoctlVolumeGetDiskExtents, IntPtr.Zero, 0,
                                    buffer, bufferSize, out var returned, IntPtr.Zero) || returned < Marshal.SizeOf<VOLUME_DISK_EXTENTS_HEADER>()) continue;
                            var header = Marshal.PtrToStructure<VOLUME_DISK_EXTENTS_HEADER>(buffer);
                            var offset = Marshal.SizeOf<VOLUME_DISK_EXTENTS_HEADER>();
                            var extentSize = Marshal.SizeOf<DISK_EXTENT>();
                            for (var index = 0; index < header.NumberOfDiskExtents && offset + extentSize <= returned; index++, offset += extentSize)
                            {
                                var extent = Marshal.PtrToStructure<DISK_EXTENT>(buffer + offset);
                                if (!result.TryGetValue((int)extent.DiskNumber, out var filesystemIds))
                                    result[(int)extent.DiskNumber] = filesystemIds = [];
                                var id = FilesystemId(drive);
                                if (!filesystemIds.Contains(id, StringComparer.Ordinal)) filesystemIds.Add(id);
                            }
                        }
                        finally { Marshal.FreeHGlobal(buffer); }
                    }
                    finally { NativeMethods.CloseHandle(handle); }
                }
                catch { }
            }
        }
        catch { }
        return result.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value.ToArray());
    }

    /// <summary>
    /// IOCTL_DISK_PERFORMANCE is a direct Windows disk-driver counter source. Devices which deny the query
    /// are simply absent, allowing the capability flag to remain honest on restricted service accounts.
    /// </summary>
    private static IReadOnlyList<RawDiskCounters> ReadDiskCounters()
    {
        var result = new List<RawDiskCounters>();
        for (var index = 0; index < 32; index++)
        {
            var handle = NativeMethods.CreateFile($@"\\.\PhysicalDrive{index}", NativeMethods.GenericRead,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite, IntPtr.Zero, NativeMethods.OpenExisting, 0, IntPtr.Zero);
            if (handle == NativeMethods.InvalidHandleValue) continue;
            try
            {
                if (!NativeMethods.DeviceIoControl(handle, NativeMethods.IoctlDiskPerformance, IntPtr.Zero, 0,
                        out DISK_PERFORMANCE performance, Marshal.SizeOf<DISK_PERFORMANCE>(), out _, IntPtr.Zero)) continue;
                // Disk performance time values are 100-nanosecond intervals. QueryTime includes the cumulative
                // wall-clock sample point; IdleTime lets the sampler derive activity without a per-request baseline.
                var queryMilliseconds = ToMilliseconds(performance.QueryTime);
                var busyMilliseconds = Math.Max(0, queryMilliseconds - ToMilliseconds(performance.IdleTime));
                result.Add(new RawDiskCounters($"windows-disk:{performance.StorageDeviceNumber}", performance.ReadCount,
                    performance.WriteCount, performance.BytesRead / 512, performance.BytesWritten / 512, busyMilliseconds, null,
                    ToMilliseconds(performance.ReadTime), ToMilliseconds(performance.WriteTime), 512));
            }
            catch { }
            finally { NativeMethods.CloseHandle(handle); }
        }
        return result;
    }

    private static IReadOnlyList<NetworkInterfaceInfoDto> ReadNetworkInfo()
    {
        var result = new List<NetworkInterfaceInfoDto>();
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.NetworkInterfaceType == NetworkInterfaceType.Loopback || network.OperationalStatus != OperationalStatus.Up) continue;
                var addresses = network.GetIPProperties().UnicastAddresses
                    .Where(x => (x.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) && !IPAddress.IsLoopback(x.Address))
                    .Select(x => x.Address.ToString()).ToArray();
                result.Add(new NetworkInterfaceInfoDto(NetworkId(network), network.Name, network.Speed > 0 ? network.Speed : null, addresses));
            }
        }
        catch { }
        return result;
    }

#pragma warning disable CA1416 // This entire source is only registered when RuntimeInformation reports Windows.
    private static IReadOnlyList<RawNetworkCounters> ReadNetworkCounters()
    {
        var result = new List<RawNetworkCounters>();
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.NetworkInterfaceType == NetworkInterfaceType.Loopback || network.OperationalStatus != OperationalStatus.Up) continue;
                try
                {
                    var stats = network.GetIPv4Statistics();
                    result.Add(new RawNetworkCounters(NetworkId(network), stats.BytesReceived, stats.BytesSent,
                        stats.UnicastPacketsReceived + stats.NonUnicastPacketsReceived,
                        stats.UnicastPacketsSent + stats.NonUnicastPacketsSent,
                        stats.IncomingPacketsWithErrors, stats.OutgoingPacketsWithErrors,
                        stats.IncomingPacketsDiscarded, stats.OutgoingPacketsDiscarded));
                }
                catch { }
            }
        }
        catch { }
        return result;
    }
#pragma warning restore CA1416

    private static string FilesystemId(DriveInfo drive) => $"fs:{drive.RootDirectory.FullName}";
    private static string NetworkId(NetworkInterface network) => $"windows-net:{network.Id}";
    private static long ToLong(FILETIME value) => ((long)value.dwHighDateTime << 32) | value.dwLowDateTime;
    private static long ToLong(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;
    private static long ToMilliseconds(long hundredNanoseconds) => Math.Max(0, hundredNanoseconds / TimeSpan.TicksPerMillisecond);

    private static class NativeMethods
    {
        public const int SystemProcessorPerformanceInformation = 8;
        public const uint GenericRead = 0x80000000;
        public const uint FileShareRead = 0x00000001;
        public const uint FileShareWrite = 0x00000002;
        public const uint OpenExisting = 3;
        public const uint IoctlDiskPerformance = 0x00070020;
        public const uint IoctlVolumeGetDiskExtents = 0x00560000;
        public static readonly IntPtr InvalidHandleValue = new(-1);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("ntdll.dll")]
        public static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(IntPtr device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize,
            out DISK_PERFORMANCE outBuffer, int outBufferSize, out uint bytesReturned, IntPtr overlapped);

        [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControlRaw(IntPtr device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize,
            IntPtr outBuffer, int outBufferSize, out uint bytesReturned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
    {
        public long IdleTime;
        public long KernelTime;
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint InterruptCount;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISK_PERFORMANCE
    {
        public long BytesRead;
        public long BytesWritten;
        public long ReadTime;
        public long WriteTime;
        public long IdleTime;
        public uint ReadCount;
        public uint WriteCount;
        public uint QueueDepth;
        public uint SplitCount;
        public long QueryTime;
        public uint StorageDeviceNumber;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public ushort[] StorageManagerName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VOLUME_DISK_EXTENTS_HEADER { public uint NumberOfDiskExtents; public uint Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISK_EXTENT { public uint DiskNumber; public long StartingOffset; public long ExtentLength; }
}
