using System.Diagnostics;

namespace RelaxKonOS.Server.Git;

/// <summary>Resolves the git executable path on the host OS (Linux: which/git; Windows: where/PATH). Does not hardcode drive letters or registry keys.</summary>
public sealed class HostGitCli : IHostGitCli
{
    private static readonly string[] LinuxCandidates = ["/usr/bin/git", "/usr/local/bin/git"];
    private static readonly string WindowsExecutable = "git.exe";

    public string? ResolveGitPath()
    {
        if (OperatingSystem.IsWindows())
            return ProbeWindows();
        if (OperatingSystem.IsLinux())
            return ProbeLinux();
        return null;
    }

    private static string? ProbeLinux()
    {
        // Try `which git` first, then fall back to common paths.
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("which", ["git"])
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(TimeSpan.FromSeconds(3));
            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                return output;
        }
        catch { /* which not available, fall through to candidates */ }

        foreach (var candidate in LinuxCandidates)
            if (File.Exists(candidate))
                return candidate;
        return null;
    }

    private static string? ProbeWindows()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("where", [WindowsExecutable])
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(TimeSpan.FromSeconds(3));
            if (process.ExitCode == 0)
            {
                var first = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(first))
                    return first;
            }
        }
        catch { /* where not available */ }
        return null;
    }
}
