using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RelaxKonOS.Server.Terminal;

/// <summary>Stable observation of an actual direct child; it never sends a signal by PID.</summary>
internal sealed class LinuxTerminalProcessExit : IDisposable
{
    private readonly SafeFileHandle _descriptor;
    private LinuxTerminalProcessExit(SafeFileHandle descriptor) => _descriptor = descriptor;

    internal static LinuxTerminalProcessExit Capture(int childPid)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64) || childPid <= 0)
            throw new IOException("terminal.process_identity_unverified");
        var raw = PidfdOpen(434, childPid, 0);
        if (raw < 0) throw new IOException("terminal.process_identity_unverified", new Win32Exception(Marshal.GetLastPInvokeError()));
        var descriptor = new SafeFileHandle((nint)raw, ownsHandle: true);
        try
        {
            var fd = checked((int)raw);
            if (ReadDescriptorPid(fd) != childPid) throw new IOException("terminal.process_identity_unverified");
            var stat = File.ReadAllText($"/proc/{childPid}/stat");
            var close = stat.LastIndexOf(')');
            if (close < 0) throw new IOException("terminal.process_identity_unverified");
            var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 20 || !int.TryParse(fields[1], CultureInfo.InvariantCulture, out var parent) || parent != Environment.ProcessId
                || !ulong.TryParse(fields[19], CultureInfo.InvariantCulture, out var started) || started == 0
                || ReadDescriptorPid(fd) != childPid)
                throw new IOException("terminal.process_identity_unverified");
            return new(descriptor);
        }
        catch { descriptor.Dispose(); throw; }
    }

    internal Task WaitForSignalAsync() => Task.Run(() =>
    {
        var descriptor = checked((int)_descriptor.DangerousGetHandle());
        var poll = new PollDescriptor { Descriptor = descriptor, Events = 1 };
        while (true)
        {
            var ready = Poll(ref poll, 1, -1);
            if (ready < 0 && Marshal.GetLastPInvokeError() == 4) continue;
            if (ready <= 0 || (poll.ReturnedEvents & (1 | 16)) == 0)
                throw new IOException("terminal.process_exit_unverified", new Win32Exception(Marshal.GetLastPInvokeError()));
            break;
        }
    });
    internal async Task<int?> PeekExitCodeAsync()
    {
        await WaitForSignalAsync();
        var descriptor = checked((uint)_descriptor.DangerousGetHandle());
        while (true)
        {
            if (WaitId(3, descriptor, out var status, 4 | 0x1000000) == 0)
                return status.Code switch { 1 => status.Status, 2 or 3 => 128 + status.Status, _ => throw new IOException("terminal.process_status_unverified") };
            var error = Marshal.GetLastPInvokeError();
            if (error == 4) continue;
            if (error == 10) return null;
            throw new IOException("terminal.process_status_unverified", new Win32Exception(error));
        }
    }

    internal async Task WaitForExitAsync()
    {
        await WaitForSignalAsync();
        var descriptor = checked((int)_descriptor.DangerousGetHandle());
        // P_PIDFD binds reaping to the same kernel process instance. ECHILD means its owner already reaped it.
        while (WaitId(3, checked((uint)descriptor), out _, 4) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == 4) continue;
            if (error == 10) break;
            throw new IOException("terminal.process_reap_unverified", new Win32Exception(error));
        }
    }
    internal void Terminate()
    {
        // The descriptor names the captured kernel instance even if its numeric PID is reused.
        if (PidfdSendSignal(424, checked((int)_descriptor.DangerousGetHandle()), 9, IntPtr.Zero, 0) != 0
            && Marshal.GetLastPInvokeError() != 3)
            throw new IOException("terminal.process_tree_signal_unverified", new Win32Exception(Marshal.GetLastPInvokeError()));
    }
    private static int ReadDescriptorPid(int descriptor)
    {
        var entry = File.ReadLines($"/proc/self/fdinfo/{descriptor}").SingleOrDefault(line => line.StartsWith("Pid:", StringComparison.Ordinal));
        return entry is not null && int.TryParse(entry.AsSpan(4).Trim(), CultureInfo.InvariantCulture, out var pid) ? pid : -1;
    }
    public void Dispose() => _descriptor.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct PollDescriptor { public int Descriptor; public short Events; public short ReturnedEvents; }
    [StructLayout(LayoutKind.Sequential, Size = 128)] private struct SignalInfo
    { public int Signal, Error, Code, Padding, Pid; public uint Uid; public int Status; }
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] private static extern long PidfdOpen(long syscall, int pid, uint flags);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] private static extern long PidfdSendSignal(long syscall, int fd, int signal, IntPtr info, uint flags);
    [DllImport("libc", EntryPoint = "poll", SetLastError = true)] private static extern int Poll(ref PollDescriptor descriptor, nuint count, int milliseconds);
    [DllImport("libc", EntryPoint = "waitid", SetLastError = true)] private static extern int WaitId(int type, uint id, out SignalInfo info, int options);
}
