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
        if (!File.Exists(executablePath)) return null;
        using var process = new Process { StartInfo = new ProcessStartInfo(executablePath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add("-v");
        try
        {
            if (!process.Start()) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var standardOut = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(standardOut, standardError, process.WaitForExitAsync(timeout.Token));
            var output = (await standardOut).Trim();
            return process.ExitCode == 0 && output.Length > 0 ? output[..Math.Min(128, output.Length)] : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch { return null; }
    }
}
