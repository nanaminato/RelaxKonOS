using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RelaxKonOS.Server.SystemPerformance;

internal static class ProcessTreeMetadata
{
    public static IReadOnlyDictionary<int, int> CaptureParentIds()
    {
        if (OperatingSystem.IsWindows()) return CaptureWindowsParentIds();
        return new Dictionary<int, int>();
    }

    public static int? ReadParentId(Process process)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var stat = File.ReadAllText($"/proc/{process.Id}/stat");
                var closeName = stat.LastIndexOf(')');
                if (closeName < 0 || closeName + 1 >= stat.Length) return null;
                var fields = stat[(closeName + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // After the parenthesized command: state is field 3, PPID is field 4.
                return fields.Length > 1 && int.TryParse(fields[1], out var parentId) ? parentId : null;
            }
            catch { return null; }
        }

        return null;
    }

    private static IReadOnlyDictionary<int, int> CaptureWindowsParentIds()
    {
        var result = new Dictionary<int, int>();
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!NativeMethods.Process32First(snapshot, ref entry)) return result;
            do
            {
                if (entry.ProcessId <= int.MaxValue && entry.ParentProcessId <= int.MaxValue)
                    result[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            } while (NativeMethods.Process32Next(snapshot, ref entry));
        }
        catch { /* Process tree is best-effort; a partial snapshot remains useful. */ }
        finally { NativeMethods.CloseHandle(snapshot); }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public ProcessEntry32() { ExecutableFile = string.Empty; }
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExecutableFile;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
