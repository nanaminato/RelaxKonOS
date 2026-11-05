extern alias modulehelper;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using modulehelper::RelaxKonOS.PrivilegedHelper;

/// <summary>Native current-account Job acceptance, without provisioning accounts or services.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsJobTreeChecks
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "relaxkon-job-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var count = 0;
        void Check(bool passed, string message)
        {
            if (!passed) throw new InvalidOperationException(message);
            Console.WriteLine($"PASS WINDOWS JOB TREE {++count}: {message}");
        }
        try
        {
            foreach (var closeHandle in new[] { false, true })
            {
                var directory = Path.Combine(root, closeHandle ? "close" : "confirm");
                Directory.CreateDirectory(directory);
                using var job = WindowsComponentJob.Create();
                using var parent = LaunchSuspended(directory, "root");
                Check(!File.Exists(Path.Combine(directory, "root.pid")), "Suspended root executes no module instructions before Job admission");
                if (!AssignProcessToJobObject(job, parent.Process))
                {
                    var error = NativeError();
                    TerminateProcess(parent.Process, 1);
                    await WaitSignaledAsync(parent.Process);
                    throw error;
                }
                if (ResumeThread(parent.Thread) == uint.MaxValue) throw NativeError();
                await WaitFileAsync(Path.Combine(directory, "leaf.pid"));
                await WaitFileAsync(Path.Combine(directory, "breakaway.txt"));
                using var leaf = OpenRecordedProcess(directory, "leaf");
                await WaitSignaledAsync(parent.Process);
                Check(WaitForSingleObject(leaf, 0) == 258 && Active(job) > 0,
                    "Root exit leaves a real detached grandchild alive inside the same Job");
                Check(await File.ReadAllTextAsync(Path.Combine(directory, "breakaway.txt")) == "denied:5",
                    "The production Job denies CREATE_BREAKAWAY_FROM_JOB before a descendant can escape");
                var outsiderDirectory = Path.Combine(directory, "outside");
                Directory.CreateDirectory(outsiderDirectory);
                using var outsider = LaunchSuspended(outsiderDirectory, "leaf");
                try
                {
                    if (ResumeThread(outsider.Thread) == uint.MaxValue) throw NativeError();
                    await WaitFileAsync(Path.Combine(outsiderDirectory, "leaf.pid"));
                    if (closeHandle)
                    {
                        job.Dispose();
                        await WaitSignaledAsync(leaf);
                        Check(true, "Closing the production KILL_ON_JOB_CLOSE handle reclaims the detached descendant");
                    }
                    else
                    {
                        await WindowsComponentJob.ConfirmExitAsync(job, default);
                        Check(Active(job) == 0 && WaitForSingleObject(leaf, 0) == 0,
                            "Production ModuleHost tree confirmation waits for native Job ActiveProcesses zero and real descendant exit");
                    }
                    Check(WaitForSingleObject(outsider.Process, 0) == 258,
                        "Tree cleanup leaves an unrelated same-account process alive");
                }
                finally
                {
                    if (WaitForSingleObject(outsider.Process, 0) == 258 && !TerminateProcess(outsider.Process, 0)) throw NativeError();
                    await WaitSignaledAsync(outsider.Process);
                }
            }
            using (var closed = WindowsComponentJob.Create())
            {
                closed.Dispose();
                try
                {
                    await WindowsComponentJob.ConfirmExitAsync(closed, default);
                    throw new InvalidOperationException("An invalid Job was reported as confirmed.");
                }
                catch (Exception error) when (error is ObjectDisposedException or Win32Exception)
                { Check(true, "An unavailable ownership handle fails tree confirmation rather than proving parent-only success"); }
            }
            Console.WriteLine($"Windows native Job tree checks passed: {count}");
            Console.WriteLine("SCOPE: current-account native Job; dedicated-account S4U and installed Helper service acceptance remain separate.");
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (Path.GetDirectoryName(resolved) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())))
                throw new IOException("Invalid fixture cleanup root.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    internal static async Task ChildAsync(string directory, string role)
    {
        File.WriteAllText(Path.Combine(directory, role + ".pid"), Environment.ProcessId.ToString());
        if (role == "leaf") { await Task.Delay(Timeout.InfiniteTimeSpan); return; }
        if (role == "root")
        {
            using var middle = LaunchSuspended(directory, "middle");
            if (ResumeThread(middle.Thread) == uint.MaxValue) throw NativeError();
            await WaitFileAsync(Path.Combine(directory, "leaf.pid"));
            await WaitFileAsync(Path.Combine(directory, "breakaway.txt"));
            return;
        }
        if (role != "middle") throw new ArgumentException("Unknown tree fixture role.");
        using var child = LaunchSuspended(directory, "leaf", extraFlags: 0x8 | 0x200); // Detached + new process group, still in Job.
        if (ResumeThread(child.Thread) == uint.MaxValue) throw NativeError();
        await WaitFileAsync(Path.Combine(directory, "leaf.pid"));
        try
        {
            using var escaped = LaunchSuspended(directory, "escape", extraFlags: 0x1000000);
            // A suspended unexpected process has executed nothing; retain its handle for exact cleanup.
            TerminateProcess(escaped.Process, 1);
            await WaitSignaledAsync(escaped.Process);
            File.WriteAllText(Path.Combine(directory, "breakaway.txt"), "unexpected-success");
        }
        catch (Win32Exception error) { File.WriteAllText(Path.Combine(directory, "breakaway.txt"), "denied:" + error.NativeErrorCode); }
    }

    private static async Task WaitFileAsync(string path)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Native fixture did not become ready: " + Path.GetFileName(path));
            await Task.Delay(20);
        }
    }
    private static SafeFileHandle OpenRecordedProcess(string directory, string role)
    {
        var pid = int.Parse(File.ReadAllText(Path.Combine(directory, role + ".pid")));
        var process = OpenProcess(0x100000 | 0x1000, false, pid);
        if (process.IsInvalid) throw NativeError();
        return process;
    }
    private static async Task WaitSignaledAsync(SafeFileHandle process)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var status = WaitForSingleObject(process, 0);
            if (status == 0) return;
            if (status != 258) throw NativeError();
            if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Native process remains alive.");
            await Task.Delay(20);
        }
    }
    private static uint Active(SafeFileHandle job)
    {
        if (!QueryInformationJobObject(job, 1, out var accounting, Marshal.SizeOf<JobAccounting>(), out _)) throw NativeError();
        return accounting.ActiveProcesses;
    }
    private sealed record NativeChild(SafeFileHandle Process, SafeFileHandle Thread) : IDisposable
    {
        public void Dispose() { Thread.Dispose(); Process.Dispose(); }
    }
    private static NativeChild LaunchSuspended(string directory, string role, uint extraFlags = 0)
    {
        var executable = Environment.ProcessPath ?? throw new IOException();
        var arguments = new List<string> { executable };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(Assembly.GetExecutingAssembly().Location);
        arguments.AddRange(["--job-tree-child", directory, role]);
        var command = new StringBuilder(string.Join(" ", arguments.Select(Quote)));
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcessW(executable, command, 0, 0, false, 0x4 | extraFlags, 0, directory, ref startup, out var process)) throw NativeError();
        return new(new(process.Process, true), new(process.Thread, true));
    }
    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    private static Win32Exception NativeError() => new(Marshal.GetLastWin32Error());
    [StructLayout(LayoutKind.Sequential)] private struct JobAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public short Show, ReservedSize; public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string application,
        StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeFileHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeFileHandle process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass,
        out JobAccounting information, int length, out int returnedLength);
}
