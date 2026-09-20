using System.Diagnostics;

namespace RelaxKonOS.Server.Docker;

/// <summary>
/// Thin wrapper over the <c>docker desktop</c> CLI. Docker Desktop owns the daemon on Windows and
/// exposes start, stop, restart, and status through its own plugin, so both the proxy feature and
/// the engine controls drive it here rather than implementing process handling twice. Child output
/// is drained and discarded: it can echo host configuration, and nothing in it is needed to decide
/// whether the command succeeded.
/// </summary>
internal static class DockerDesktopCli
{
    /// <summary>
    /// Runs <c>docker desktop &lt;command&gt;</c>. Returns false when the plugin is missing, exits
    /// non-zero, or exceeds <paramref name="timeout"/>; the caller turns that into a problem code.
    /// </summary>
    internal static async Task<bool> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            } };
            process.StartInfo.ArgumentList.Add("desktop");
            process.StartInfo.ArgumentList.Add(command);
            if (!process.Start()) return false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                TryStop(process);
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(output, error);
                return false;
            }
            await Task.WhenAll(output, error);
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static void TryStop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* The process exited between the checks. */ }
    }
}
