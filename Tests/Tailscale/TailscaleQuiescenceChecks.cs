using System.Diagnostics;
using RelaxKonOS.PrivilegedHelper;

internal static class TailscaleQuiescenceChecks
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        { Console.WriteLine("SKIP Tailscale CLI quiescence probe: unsupported test platform."); return; }
        var folder = Directory.CreateTempSubdirectory("relaxkonos-tailscale-quiescence-");
        try
        {
            // A renamed, trusted OS utility is a controlled CLI name probe, never a real Tailscale invocation.
            var source = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") : "/usr/bin/sleep";
            var path = Path.Combine(folder.FullName, OperatingSystem.IsWindows() ? "tailscale.exe" : "tailscale");
            File.Copy(source, path);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (OperatingSystem.IsWindows())
            {
                start.ArgumentList.Add("/d"); start.ArgumentList.Add("/q"); start.ArgumentList.Add("/c");
                start.ArgumentList.Add("set /p RELAXKONOS_QUIESCENCE_PROBE=");
            }
            else start.ArgumentList.Add("30");
            using var child = Process.Start(start) ?? throw new Exception("Quiescence probe failed to start");
            try
            {
                await Task.Delay(100);
                if (child.HasExited || !string.Equals(child.ProcessName, "tailscale", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Quiescence probe is not a live CLI instance");
                if (await new TailscaleNativeControl().IsQuiescentAsync(default))
                    throw new Exception("Live CLI instance was missed by the production guard");
                Console.WriteLine("2 Tailscale native process-quiescence checks passed.");
            }
            finally
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            // Windows can retain the exited image mapping briefly after WaitForExit completes.
            for (var attempt = 0; ; attempt++)
            {
                try { folder.Delete(true); break; }
                catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows() && attempt < 7) { await Task.Delay(50); }
                catch (IOException) when (OperatingSystem.IsWindows() && attempt < 7) { await Task.Delay(50); }
            }
        }
    }
}
