using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Server.Installations;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Certificate;
using RelaxKonOS.Server.Docker;

namespace RelaxKonOS.Server.WebServer;


internal sealed partial class NginxWebServerManager
{
    private static bool IsNginxRunning(string executablePath)
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (IsNginxProcess(process, executablePath)) return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>Uses the PID written by the RelaxKonOS-owned configuration instead of a host-wide
    /// process-name scan. This keeps the managed instance independent from other host Nginx processes.</summary>
    private static bool IsManagedNginxRunning(ManagedLayout layout)
    {
        var pidPath = Path.Combine(layout.Root, "logs", "nginx.pid");
        var pid = 0;
        try
        {
            if (!File.Exists(pidPath) || IsSymbolicLink(pidPath)
                || !int.TryParse(File.ReadAllText(pidPath).Trim(), out pid) || pid <= 0) return false;
            if (OperatingSystem.IsLinux() && !IsPosixProcessAlive(pid)) return false;
            using var process = Process.GetProcessById(pid);
            if (IsNginxProcess(process, layout.ExecutablePath)) return true;
            // Some Ubuntu/systemd configurations deny both Process.MainModule and
            // /proc/<pid>/exe to the RelaxKonOS service. The PID is written to a regular,
            // RelaxKonOS-owned file by this exact configuration, so a live Nginx process at
            // that PID remains a reliable managed-instance signal when image inspection is
            // unavailable. This is intentionally not used for non-managed instances.
            return !process.HasExited && IsNginxProcessName(process.ProcessName);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (System.ComponentModel.Win32Exception) when (OperatingSystem.IsLinux()) { return IsPosixProcessAlive(pid); }
    }

    private static bool IsNginxProcess(Process process, string executablePath)
    {
        try
        {
            if (process.HasExited || !IsNginxProcessName(process.ProcessName)) return false;
        }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (NotSupportedException) { return false; }

        // Process.MainModule is not consistently readable for a daemon started by a Linux
        // service, even when the PID file is readable.  /proc/<pid>/exe identifies the same
        // executable without relying on that API, so a successfully started managed Nginx is
        // not reported as stopped after the two-second readiness check.
        if (OperatingSystem.IsLinux())
        {
            try { return ExecutablePathsMatch($"/proc/{process.Id}/exe", executablePath); }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
            catch (NotSupportedException) { return false; }
        }

        try
        {
            var processExecutable = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(processExecutable) && ExecutablePathsMatch(processExecutable, executablePath);
        }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (NotSupportedException) { return false; }
    }

    /// <summary>On Linux, use the kernel's process-existence check rather than /proc metadata.
    /// Signal 0 does not alter the target process. An EPERM result still proves it exists.</summary>
    private static bool IsPosixProcessAlive(int pid)
    {
        if (!OperatingSystem.IsLinux()) return false;
        if (Kill(pid, 0) == 0) return true;
        return Marshal.GetLastPInvokeError() == 1; // EPERM
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static extern int Kill(int pid, int signal);

    private static bool IsNginxProcessName(string processName) =>
        string.Equals(processName, "nginx", StringComparison.OrdinalIgnoreCase)
        || processName.StartsWith("nginx:", StringComparison.OrdinalIgnoreCase);

    private static bool ExecutablePathsMatch(string left, string right)
    {
        try
        {
            var normalizedLeft = ResolveExecutablePath(left);
            var normalizedRight = ResolveExecutablePath(right);
            return string.Equals(normalizedLeft, normalizedRight,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string ResolveExecutablePath(string path)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        return Path.GetFullPath(file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file.FullName);
    }

    private static async Task<bool> WaitForManagedNginxAsync(ManagedLayout layout, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (IsManagedNginxRunning(layout)) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
        return false;
    }

    private async Task<CommandResult> RunNginxAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows() && !arguments.SequenceEqual(new[] { "-V" }) && !arguments.SequenceEqual(new[] { "-v" }))
        {
            var layout = GetManagedLayout();
            if (!string.Equals(Path.GetFullPath(executable), layout.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                return new(false, "Windows privileged Nginx operations require the Helper-managed instance.");
            // Only the server's closed command shapes are translated; the Helper never receives arguments.
            var tail = arguments.Count >= 4 && arguments[0] == "-p" && arguments[2] == "-c"
                && Path.GetFullPath(arguments[1]) == layout.Root && Path.GetFullPath(arguments[3]) == layout.ConfigurationPath
                ? arguments.Skip(4).ToArray() : arguments.ToArray();
            ManagedRuntimeAction? action = tail.Length == 0 ? ManagedRuntimeAction.Start
                : tail.SequenceEqual(new[] { "-t" }) ? ManagedRuntimeAction.Test
                : tail.SequenceEqual(new[] { "-s", "quit" }) ? ManagedRuntimeAction.Stop
                : tail.SequenceEqual(new[] { "-s", "reload" }) ? ManagedRuntimeAction.Reload : null;
            if (action is null) return new(false, "Unsupported Windows Nginx operation.");
            var result = await privilegedNginx.ApplyWindowsRuntimeAsync(action.Value, cancellationToken: cancellationToken);
            return new(result.Success, result.Success ? "" : ToWebServerProblem(result.ProblemCode, "webserver.lifecycle_failed"));
        }
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = executable, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            logger.LogDebug("Running Nginx command. Executable={Executable}, Arguments={Arguments}", executable, string.Join(' ', arguments));
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var registration = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            await process.WaitForExitAsync(cancellationToken);
            var text = (await output) + (await error);
            if (process.ExitCode == 0)
                logger.LogDebug("Nginx command completed. Executable={Executable}, Arguments={Arguments}", executable, string.Join(' ', arguments));
            else
                logger.LogWarning("Nginx command failed. Executable={Executable}, Arguments={Arguments}, ExitCode={ExitCode}, Output={Output}", executable, string.Join(' ', arguments), process.ExitCode, CommandOutputForLog(text));
            return new CommandResult(process.ExitCode == 0, text);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to start Nginx command. Executable={Executable}", executable);
            return new CommandResult(false, "");
        }
    }

    private static string CommandOutputForLog(string output)
    {
        const int maximumLength = 4_096;
        if (string.IsNullOrWhiteSpace(output)) return "<no output>";
        var trimmed = output.Trim();
        return trimmed.Length <= maximumLength ? trimmed : $"{trimmed[..maximumLength]}…";
    }

    private static string PrivilegedOutputForLog(PrivilegedOperationResult result)
    {
        if (string.IsNullOrWhiteSpace(result.OutputBase64)) return "<no output>";
        try { return CommandOutputForLog(Encoding.UTF8.GetString(Convert.FromBase64String(result.OutputBase64))); }
        catch (FormatException) { return "<invalid helper output>"; }
    }

    private static string? FirstWindowsVersionInSection(string page, string startHeading, string endHeading)
    {
        var start = page.IndexOf(startHeading, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var end = page.IndexOf(endHeading, start + startHeading.Length, StringComparison.OrdinalIgnoreCase);
        var section = page[start..(end < 0 ? page.Length : end)];
        return WindowsDownloadVersionPattern().Match(section) is { Success: true } match ? match.Groups["version"].Value : null;
    }

    [GeneratedRegex("--conf-path=(?:\\\"(?<path>[^\\\"]+)\\\"|(?<path>[^\\s]+))", RegexOptions.CultureInvariant)]
    private static partial Regex ConfigurationPathPattern();
    [GeneratedRegex("nginx/(?<version>[^\\s]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();
    [GeneratedRegex("^1\\.(?:[0-9]{1,3})\\.(?:[0-9]{1,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsVersionPattern();
    [GeneratedRegex("nginx/Windows[- ](?<version>1\\.[0-9]{1,3}\\.[0-9]{1,3})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WindowsDownloadVersionPattern();
    [GeneratedRegex("^[0-9][0-9A-Za-z.+:~\\-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex LinuxPackageVersionPattern();
    [GeneratedRegex("^\\s*include\\s+(?<path>[^;]+\\.conf)\\s*;", RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex IncludePattern();
    [GeneratedRegex("^\\s*http\\s*\\{", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HttpBlockPattern();
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex SiteIdPattern();
    [GeneratedRegex("^/(?:[A-Za-z0-9._~-]+/)*$", RegexOptions.CultureInvariant)]
    private static partial Regex RoutePathPattern();
    [GeneratedRegex("^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();

    private sealed record CommandResult(bool Success, string Output);
}
