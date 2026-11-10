using System.ComponentModel;
using System.Diagnostics;

namespace RelaxKonOS.Server.Proxy.Mihomo;

public interface IMihomoRuntimeProbe
{
    Task<string?> GetVersionAsync(string executablePath, CancellationToken cancellationToken);
}

/// <summary>Runs a fixed Mihomo version probe. Paths are resolved by the runtime manager, never supplied to an API.</summary>
public sealed class MihomoRuntimeProbe : IMihomoRuntimeProbe
{
    public async Task<string?> GetVersionAsync(string executablePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(executablePath)) return null;
        using var process = new Process { StartInfo = new ProcessStartInfo(executablePath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add("-v");
        try
        {
            if (!process.Start()) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var standardOut = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            var completion = Task.WhenAll(standardOut, standardError, process.WaitForExitAsync());
            try { await completion.WaitAsync(timeout.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception or AggregateException)
                { /* Exit may have raced the signal. Retain the probe until exit and EOF are observed. */ }
                await completion;
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
            var output = (await standardOut).Trim();
            return process.ExitCode == 0 && output.Length > 0 ? output[..Math.Min(128, output.Length)] : null;
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }
}
