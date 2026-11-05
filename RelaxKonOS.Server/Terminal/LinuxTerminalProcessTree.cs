using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RelaxKonOS.Server.Terminal;

/// <summary>Only for a dedicated terminal worker. Never installed on the shared Server process.</summary>
internal sealed class LinuxTerminalProcessTree
{
    internal LinuxTerminalProcessTree()
    {
        if (!OperatingSystem.IsLinux() || Children().Length != 0)
            throw new IOException("terminal.worker_not_exclusive");
        if (Prctl(36, 1, 0, 0, 0) != 0) throw new IOException("terminal.subreaper_unavailable", new Win32Exception(Marshal.GetLastPInvokeError()));
    }
    internal async Task DrainAsync()
    {
        // With the root already stopped, each remaining descendant is either our direct child,
        // or has an ancestor which is. Killing/reaping the frontier repeatedly includes setsid
        // and double-fork descendants. No shared-UID or process-group enumeration is used.
        while (true)
        {
            var children = Children();
            foreach (var pid in children)
            {
                LinuxTerminalProcessExit? child = null;
                try { child = LinuxTerminalProcessExit.Capture(pid); }
                catch (IOException) when (!Directory.Exists($"/proc/{pid}")) { continue; }
                using (child)
                {
                    child.Terminate();
                    await child.WaitForExitAsync();
                }
            }
            // ECHILD is the kernel proof that the exclusive subreaper has no live or zombie
            // child left. A zero pid with success means there is still a live frontier.
            if (WaitId(0, 0, out var info, 4 | 1) != 0)
            {
                if (Marshal.GetLastPInvokeError() == 10) return;
                if (Marshal.GetLastPInvokeError() == 4) continue;
                throw new IOException("terminal.process_tree_unverified", new Win32Exception(Marshal.GetLastPInvokeError()));
            }
            await Task.Delay(5);
        }
    }
    private static int[] Children()
    {
        var children = new HashSet<int>();
        foreach (var path in Directory.EnumerateDirectories("/proc/self/task"))
        {
            string text;
            try { text = File.ReadAllText(Path.Combine(path, "children")); }
            catch (IOException) when (!Directory.Exists(path)) { continue; }
            foreach (var item in text.Split(' ', StringSplitOptions.RemoveEmptyEntries)) children.Add(int.Parse(item));
        }
        return children.ToArray();
    }
    [StructLayout(LayoutKind.Sequential, Size = 128)] private struct SignalInfo { public int Signal; }
    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)] private static extern int Prctl(int operation, ulong argument, ulong arg3, ulong arg4, ulong arg5);
    [DllImport("libc", EntryPoint = "waitid", SetLastError = true)] private static extern int WaitId(int type, uint id, out SignalInfo info, int options);
}
