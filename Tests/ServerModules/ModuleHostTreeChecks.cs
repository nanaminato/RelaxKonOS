using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using RelaxKonOS.ServerModuleHost;

internal static class ModuleHostTreeChecks
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux() || geteuid() != 0)
            throw new InvalidOperationException("Native ModuleHost UID-tree acceptance requires Linux root; no mock or skip substitutes this check.");
        foreach (var mode in new[] { "normal", "root-first", "nonzero", "disconnect" })
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            info.ArgumentList.Add("--module-host-tree-worker"); info.ArgumentList.Add(mode);
            using var worker = Process.Start(info) ?? throw new IOException();
            var output = worker.StandardOutput.ReadToEndAsync(); var error = worker.StandardError.ReadToEndAsync();
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            Console.Write(await output); Console.Error.Write(await error);
            if (worker.ExitCode != 0) throw new InvalidOperationException("Native ModuleHost tree worker failed: " + mode);
        }
        Console.WriteLine("ModuleHost native UID process-tree checks passed: 4 scenarios.");
    }

    internal static async Task RunWorkerAsync(string mode)
    {
        if (!OperatingSystem.IsLinux() || geteuid() != 0 || mode is not ("normal" or "root-first" or "nonzero" or "disconnect"))
            throw new InvalidOperationException("Invalid native ModuleHost tree worker.");
        var uid = UnusedUid();
        // This dedicated child launcher is the same native reaper/UID cleanup used by --isolated.
        // A numeric UID needs no local account, sudo rule, service installation or host configuration.
        LinuxModuleHostProcessTree.EnableDescendantReaping();
        var info = new ProcessStartInfo("/usr/bin/setpriv") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "--reuid", uid.ToString(), "--regid", uid.ToString(), "--clear-groups", "--no-new-privs", "--", "/bin/sh", "-c", Script })
            info.ArgumentList.Add(argument);
        info.Environment.Clear(); info.Environment["PATH"] = "/usr/bin:/bin";
        using var root = Process.Start(info) ?? throw new IOException();
        var errors = root.StandardError.ReadToEndAsync();
        using var unrelated = Process.Start(new ProcessStartInfo("/usr/bin/sleep", "120") { UseShellExecute = false }) ?? throw new IOException();
        var pids = new Dictionary<string, int>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var line = await root.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) ?? throw new IOException("Missing descendant readiness.");
                var values = line.Split(':'); pids.Add(values[0], int.Parse(values[1]));
            }
            if (pids.Count != 4 || pids.Values.Distinct().Count() != 4 || !LinuxModuleHostProcessTree.HasIdentityProcess(uid)) throw new IOException("Real tree was not created.");
            foreach (var pid in pids.Values)
            {
                var identity = File.ReadLines($"/proc/{pid}/status").Single(line => line.StartsWith("Uid:", StringComparison.Ordinal));
                if (uint.Parse(identity.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1]) != uid) throw new IOException("Descendant escaped dedicated UID.");
            }
            var detached = File.ReadAllText($"/proc/{pids["detached"]}/stat").Split(')')[^1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var ignored = File.ReadLines($"/proc/{pids["detached"]}/status").Single(line => line.StartsWith("SigIgn:", StringComparison.Ordinal)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1];
            if (int.Parse(detached[3]) != pids["detached"] || (Convert.ToUInt64(ignored, 16) & (1UL << 14)) == 0) throw new IOException("Detached process did not establish its own session and ignore TERM.");
            if (mode == "root-first") root.Kill();
            else if (mode == "disconnect") root.StandardInput.Close();
            else { await root.StandardInput.WriteLineAsync(mode == "nonzero" ? "7" : "0"); await root.StandardInput.FlushAsync(); }
            await root.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (root.ExitCode != (mode == "nonzero" ? 7 : mode == "root-first" ? 137 : 0)) throw new IOException("Root outcome mismatch.");
            if (!LinuxModuleHostProcessTree.HasIdentityProcess(uid)) throw new IOException("Root-first fixture did not leave a live detached tree.");
            await LinuxModuleHostProcessTree.StopIdentityAsync(uid);
            if (unrelated.HasExited) throw new IOException("Unrelated process was terminated by module cleanup.");
            if (LinuxModuleHostProcessTree.HasIdentityProcess(uid) || pids.Values.Any(pid => Directory.Exists($"/proc/{pid}")))
                throw new IOException("Owned native processes or adopted zombies remain after cleanup.");
            // The root result remains a separate fact; tree cleanup must not turn exit 7 into success.
            Console.WriteLine($"PASS MODULEHOST NATIVE TREE {mode}: root exit={root.ExitCode}; exclusive uid={uid}; four actual processes including detached nested descendants stopped and reaped; no /proc entries remain.");
        }
        finally
        {
            try
            {
                if (!root.HasExited) { root.Kill(entireProcessTree: true); await root.WaitForExitAsync(); }
                await LinuxModuleHostProcessTree.StopIdentityAsync(uid);
                await errors.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { if (!unrelated.HasExited) { unrelated.Kill(); await unrelated.WaitForExitAsync(); } }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static uint UnusedUid()
    {
        for (var i = 0; i < 100; i++)
        {
            var uid = (uint)Random.Shared.Next(200000, 1000000);
            if (getpwuid(uid) == IntPtr.Zero && !LinuxModuleHostProcessTree.HasIdentityProcess(uid)) return uid;
        }
        throw new IOException("No unused numeric UID for native fixture.");
    }

    private const string Script = """
        printf 'root:%s\n' "$$"
        /bin/sh -c 'printf "child:%s\n" "$$"; exec /usr/bin/sleep 120' &
        /usr/bin/setsid /bin/sh -c 'trap "" TERM; printf "detached:%s\n" "$$"; /bin/sh -c '\''printf "grandchild:%s\n" "$$"; exec /usr/bin/sleep 120'\'' & wait' &
        read code || code=0
        exit "$code"
        """;
    [DllImport("libc")] private static extern uint geteuid();
    [DllImport("libc")] private static extern IntPtr getpwuid(uint uid);
}
