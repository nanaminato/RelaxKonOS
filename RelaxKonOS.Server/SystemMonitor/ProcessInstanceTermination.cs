using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.Server.SystemMonitor;

/// <summary>Checks identity and terminates through the same OS handle, never a second PID lookup.</summary>
public static class ProcessInstanceTermination
{
    public static KillProcessResultDto Terminate(int pid, DateTimeOffset expectedStartTime, CancellationToken cancellationToken)
    {
        if (pid <= 0 || expectedStartTime == default) return Failed("process.invalid_instance");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (OperatingSystem.IsWindows()) return Windows(pid, expectedStartTime, cancellationToken);
            if (OperatingSystem.IsLinux()) return Linux(pid, expectedStartTime, cancellationToken);
            return Failed("process.platform_unsupported");
        }
        catch (OperationCanceledException) { throw; }
        catch (ArgumentException) { return Failed("process.not_found"); }
        catch (Win32Exception error) { return NativeFailure(error.NativeErrorCode); }
        catch (InvalidOperationException) { return Failed("process.instance_unverifiable"); }
        catch (IOException) { return Failed("process.instance_unverifiable"); }
    }
    private static KillProcessResultDto Failed(string code, bool elevation = false) => new(false, elevation, code, null);
    private static KillProcessResultDto NativeFailure(int error) => error switch
    {
        1 or 13 when OperatingSystem.IsLinux() => Failed("process.permission_denied", true),
        5 when OperatingSystem.IsWindows() => Failed("process.permission_denied", true),
        87 when OperatingSystem.IsWindows() => Failed("process.not_found"),
        3 when OperatingSystem.IsLinux() => Failed("process.not_found"),
        38 when OperatingSystem.IsLinux() => Failed("process.platform_unsupported"),
        _ => Failed("process.operation_failed"),
    };

    [SupportedOSPlatform("windows")]
    private static KillProcessResultDto Windows(int pid, DateTimeOffset expected, CancellationToken ct)
    {
        // QUERY_LIMITED_INFORMATION | TERMINATE | SYNCHRONIZE: creation time, termination and
        // exit observation all refer to this immutable kernel object even if its PID is recycled.
        using var process = OpenProcess(0x1000 | 0x0001 | 0x00100000, false, pid);
        if (process.IsInvalid) return NativeFailure(Marshal.GetLastPInvokeError());
        if (WaitForSingleObject(process, 0) == 0) return Failed("process.not_found");
        if (!GetProcessTimes(process, out var creation, out _, out _, out _)) return NativeFailure(Marshal.GetLastPInvokeError());
        if (new DateTimeOffset(DateTime.FromFileTimeUtc(creation)) != expected) return Failed("process.instance_changed");
        ct.ThrowIfCancellationRequested();
        if (!TerminateProcess(process, 1)) return NativeFailure(Marshal.GetLastPInvokeError());
        return WaitForSingleObject(process, 3000) == 0 ? new(true, false, "", null) : Failed("process.termination_unverified");
    }

    [SupportedOSPlatform("linux")]
    private static KillProcessResultDto Linux(int pid, DateTimeOffset expected, CancellationToken ct)
    {
        // Current supported Linux hosts are x64/arm64. pidfd is required; no PID kill fallback.
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)) return Failed("process.platform_unsupported");
        var descriptor = PidfdOpen(434, pid, 0);
        if (descriptor < 0) return NativeFailure(Marshal.GetLastPInvokeError());
        using var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        using var process = Process.GetProcessById(pid);
        // Acquire pidfd first. If the PID disappeared/reappeared before this lookup, its new
        // start time cannot match the original instance, and no signal is sent.
        if (new DateTimeOffset(process.StartTime.ToUniversalTime()) != expected) return Failed("process.instance_changed");
        ct.ThrowIfCancellationRequested();
        if (PidfdSignal(424, checked((int)descriptor), 9, nint.Zero, 0) < 0) return NativeFailure(Marshal.GetLastPInvokeError());
        var poll = new PollDescriptor { Descriptor = checked((int)descriptor), Events = 1 };
        var ready = Poll(ref poll, 1, 3000);
        return ready > 0 && (poll.ReturnedEvents & (1 | 16)) != 0 ? new(true, false, "", null) : Failed("process.termination_unverified");
    }
    [StructLayout(LayoutKind.Sequential)] private struct PollDescriptor { public int Descriptor; public short Events; public short ReturnedEvents; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] private static extern long PidfdOpen(long number, int pid, uint flags);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] private static extern long PidfdSignal(long number, int descriptor, int signal, nint info, uint flags);
    [DllImport("libc", EntryPoint = "poll", SetLastError = true)] private static extern int Poll(ref PollDescriptor descriptor, nuint count, int milliseconds);
}
