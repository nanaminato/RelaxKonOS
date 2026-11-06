using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

/// <summary>Independent kernel observation for the real test-created instance, never a cached Unix Process.HasExited verdict.</summary>
internal sealed class TerminalProcessProbe : IDisposable
{
    private readonly Process _process;
    private readonly SafeFileHandle? _pidfd;
    private readonly string? _started;
    internal int Id => _process.Id;
    internal bool CachedHasExited => _process.HasExited;
    internal TerminalProcessProbe(Process process)
    {
        _process = process;
        if (OperatingSystem.IsLinux())
        {
            var raw = PidfdOpen(434, Id, 0);
            if (raw < 0) throw new IOException("Unable to bind the real fixture process.", new Win32Exception(Marshal.GetLastPInvokeError()));
            _pidfd = new((nint)raw, ownsHandle: true);
            var fd = checked((int)raw);
            if (DescriptorPid(fd) != Id || (_started = Instance(Id)) is null || DescriptorPid(fd) != Id)
            { _pidfd.Dispose(); throw new IOException("Fixture identity changed before observation was bound."); }
        }
        else _ = process.Handle;
    }
    internal bool HasExited
    {
        get
        {
            if (_pidfd is null) return _process.HasExited;
            var descriptor = new PollDescriptor { Descriptor = checked((int)_pidfd.DangerousGetHandle()), Events = 1 };
            int ready;
            do { ready = Poll(ref descriptor, 1, 0); } while (ready < 0 && Marshal.GetLastPInvokeError() == 4);
            if (ready < 0) throw new IOException("Fixture pidfd poll failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
            return ready > 0 && (descriptor.ReturnedEvents & (1 | 16)) != 0;
        }
    }
    internal bool IsReaped => _pidfd is null || !Directory.Exists($"/proc/{Id}") || Instance(Id) is { } current && current != _started;
    internal void Kill()
    {
        if (_pidfd is null) { if (!_process.HasExited) _process.Kill(); return; }
        if (PidfdSendSignal(424, checked((int)_pidfd.DangerousGetHandle()), 9, IntPtr.Zero, 0) != 0 && Marshal.GetLastPInvokeError() != 3)
            throw new IOException("Fixture pidfd cleanup failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
    }
    // Only used for the sentinel created by this exact .NET parent, not for worker-owned descendants.
    internal async Task CleanSentinelAsync()
    {
        if (!HasExited) _process.Kill(true);
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
    public void Dispose() { _pidfd?.Dispose(); _process.Dispose(); }
    private static int DescriptorPid(int descriptor)
    {
        var line = File.ReadLines($"/proc/self/fdinfo/{descriptor}").Single(item => item.StartsWith("Pid:", StringComparison.Ordinal));
        return int.Parse(line.AsSpan(4).Trim());
    }
    private static string? Instance(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19];
        }
        catch (IOException) { return null; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct PollDescriptor { public int Descriptor; public short Events; public short ReturnedEvents; }
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] private static extern long PidfdOpen(long syscall, int pid, uint flags);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] private static extern long PidfdSendSignal(long syscall, int descriptor, int signal, IntPtr info, uint flags);
    [DllImport("libc", EntryPoint = "poll", SetLastError = true)] private static extern int Poll(ref PollDescriptor descriptor, nuint count, int timeout);
}
