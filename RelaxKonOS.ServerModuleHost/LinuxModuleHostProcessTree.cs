using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RelaxKonOS.ServerModuleHost;

/// <summary>The isolated launcher is the only reaper for its exclusive module UID.</summary>
[SupportedOSPlatform("linux")]
internal static class LinuxModuleHostProcessTree
{
    internal static void EnableDescendantReaping()
    {
        // Do this in the dedicated launcher, never the long-lived Server process. Detached and
        // double-forked descendants must be adopted here rather than abandoned to PID 1.
        if (prctl(36, 1, 0, 0, 0) != 0)
            throw new IOException("module-host-subreaper-unavailable", new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    internal static bool HasIdentityProcess(uint uid) => ReadIdentityProcesses(uid).Any();

    internal static async Task StopIdentityAsync(uint uid)
    {
        if (uid == 0 || uid == geteuid()) throw new UnauthorizedAccessException();
        var info = new ProcessStartInfo("/usr/bin/pkill") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var value in new[] { "--signal", "KILL", "--uid", uid.ToString(System.Globalization.CultureInfo.InvariantCulture) }) info.ArgumentList.Add(value);
        using (var process = Process.Start(info) ?? throw new IOException())
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode is not (0 or 1)) throw new IOException("module-host-process-tree-stop-failed");
        }
        for (var attempt = 0; attempt < 120; attempt++)
        {
            ReapAdoptedChildren(uid);
            if (!HasIdentityProcess(uid)) return;
            await Task.Delay(100);
        }
        throw new IOException("module-host-process-tree-exit-unconfirmed");
    }

    private static void ReapAdoptedChildren(uint uid)
    {
        foreach (var (pid, parent, state) in ReadIdentityProcesses(uid))
        {
            if (parent != Environment.ProcessId || state != 'Z') continue;
            // waitpid cannot act on a reused unrelated process. Only adopted, already exited
            // direct children of this launcher are reaped, after its Process-owned root was awaited.
            while (waitpid(pid, out _, 1) < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == 4) continue;
                if (error == 10) break; // Another actual owner already reaped this child.
                throw new IOException("module-host-process-tree-reap-failed", new Win32Exception(error));
            }
        }
    }

    private static IEnumerable<(int Pid, int Parent, char State)> ReadIdentityProcesses(uint uid)
    {
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid)) continue;
            (int, int, char)? owned = null;
            try
            {
                var identity = File.ReadLines(Path.Combine(directory, "status")).FirstOrDefault(line => line.StartsWith("Uid:", StringComparison.Ordinal));
                if (identity is null || !uint.TryParse(identity.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1), out var actual))
                    throw new IOException("module-host-process-identity-unreadable");
                if (actual != uid) continue;
                var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 2 || fields[0].Length != 1 || !int.TryParse(fields[1], out var parent)) throw new IOException();
                owned = (pid, parent, fields[0][0]);
            }
            catch (IOException) when (!Directory.Exists(directory)) { }
            if (owned is { } value) yield return value;
        }
    }

    [DllImport("libc", SetLastError = true)] private static extern int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);
    [DllImport("libc", SetLastError = true)] private static extern int waitpid(int pid, out int status, int options);
    [DllImport("libc")] private static extern uint geteuid();
}
