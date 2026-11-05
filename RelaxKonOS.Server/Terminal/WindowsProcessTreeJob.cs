using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace RelaxKonOS.Platform.Processes;

/// <summary>Native Windows Job ownership and exit proof shared by ModuleHost and terminal workers.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsProcessTreeJob
{
    internal static SafeFileHandle Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<ExtendedLimits>());
        try
        {
            Marshal.StructureToPtr(limits, pointer, false);
            if (!SetInformationJobObject(job, 9, pointer, (uint)Marshal.SizeOf<ExtendedLimits>()))
            { job.Dispose(); throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
            return job;
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    internal static void Attach(SafeFileHandle job, Process process)
    {
        if (!AssignProcessToJobObject(job, process.Handle))
        {
            if (!process.HasExited) process.Kill(true);
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }
    internal static void Attach(SafeFileHandle job, SafeFileHandle process)
    {
        if (!AssignNativeProcess(job, process))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    internal static void Terminate(SafeFileHandle job)
    {
        if (!TerminateJobObject(job, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    internal static async Task ConfirmExitAsync(SafeFileHandle job, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        // Job accounting can reach zero before an exiting process object becomes signaled. Bind
        // the currently observable members before termination and retain those native handles too.
        var members = CaptureMembers(job, deadline.Token);
        try
        {
            Terminate(job);
            while (true)
            {
                if (!QueryInformationJobObject(job, 1, out var accounting, Marshal.SizeOf<JobAccounting>(), out _))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                if (accounting.ActiveProcesses == 0) break;
                await Task.Delay(50, deadline.Token);
            }
            foreach (var member in members)
            {
                while (true)
                {
                    var result = WaitForSingleObject(member, 0);
                    if (result == 0) break;
                    if (result != 258) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    await Task.Delay(20, deadline.Token);
                }
            }
        }
        finally { foreach (var member in members) member.Dispose(); }
    }
    private static List<SafeFileHandle> CaptureMembers(SafeFileHandle job, CancellationToken cancellationToken)
    {
        var members = new List<SafeFileHandle>();
        try
        {
            for (var capacity = 128; capacity <= 65536; capacity *= 2)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var size = checked(8 + capacity * IntPtr.Size);
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!QueryProcessList(job, 3, buffer, size, out _))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error == 234) continue; // ERROR_MORE_DATA, with a bounded retry allocation.
                        throw new System.ComponentModel.Win32Exception(error);
                    }
                    var count = Marshal.ReadInt32(buffer, 4);
                    if (count < 0 || count > capacity) throw new IOException("module-host-process-tree-exit-unconfirmed");
                    for (var index = 0; index < count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var pid = checked((uint)Marshal.ReadIntPtr(buffer, 8 + index * IntPtr.Size).ToInt64());
                        var process = OpenProcess(0x100000 | 0x1000, false, pid);
                        if (process.IsInvalid)
                        {
                            var error = Marshal.GetLastWin32Error();
                            process.Dispose();
                            if (error == 87) continue; // That process instance has already disappeared.
                            throw new System.ComponentModel.Win32Exception(error);
                        }
                        if (!IsProcessInJob(process, job, out var belongs))
                        {
                            var error = Marshal.GetLastWin32Error(); process.Dispose();
                            throw new System.ComponentModel.Win32Exception(error);
                        }
                        if (belongs) members.Add(process);
                        else process.Dispose(); // PID reused after the Job snapshot; do not touch the outsider.
                    }
                    return members;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            throw new IOException("module-host-process-tree-exit-unconfirmed");
        }
        catch { foreach (var member in members) member.Dispose(); throw; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct JobAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, IntPtr information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", EntryPoint = "AssignProcessToJobObject", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignNativeProcess(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass,
        out JobAccounting information, int length, out int returnedLength);
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)] private static extern bool QueryProcessList(SafeFileHandle job,
        int informationClass, IntPtr information, int length, out int returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(SafeFileHandle process, SafeFileHandle job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeFileHandle process, uint milliseconds);
}
