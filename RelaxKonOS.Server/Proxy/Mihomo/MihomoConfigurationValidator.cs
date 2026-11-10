using System.Diagnostics;
using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Runs the native configuration checker while the original official archive pointer is locked.</summary>
internal sealed class MihomoConfigurationValidator(IProxyPlatformPaths paths, MihomoOfficialRuntimeState official,
    IProxyDiagnosticLogStore diagnostics) : IMihomoConfigurationValidator
{
    /// <summary>
    /// Validates a candidate profile with the active, verified managed binary. This deliberately
    /// does not depend on the controller or service being running: importing a subscription must
    /// be possible before the profile is activated.
    /// </summary>
    public async Task<string?> ValidateAsync(string configurationPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configurationPath) || !Path.IsPathFullyQualified(configurationPath) || !File.Exists(configurationPath))
            return ProxyProblemCodes.ConfigInvalid;

        await official.Gate.WaitAsync(cancellationToken);
        try
        {
            var state = await official.ReadAsync(cancellationToken);
            var executable = state?.ActiveVersion is { Length: > 0 } active ? official.ExecutablePath(active) : null;
            if (executable is null || !File.Exists(executable)) return ProxyProblemCodes.RuntimeNotInstalled;

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            // The executable, arguments, and profile path are all Server-derived; no API input
            // is executed as a command.
            process.StartInfo.ArgumentList.Add("-t");
            process.StartInfo.ArgumentList.Add("-d");
            process.StartInfo.ArgumentList.Add(paths.GetEngineDataDirectory(MihomoEngine.Id));
            process.StartInfo.ArgumentList.Add("-f");
            process.StartInfo.ArgumentList.Add(configurationPath);
            try
            {
                if (!process.Start()) return ProxyProblemCodes.RuntimeHealthCheckFailed;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                // Keep draining output after the deadline so a timed-out validation still leaves
                // a safe diagnostic category. Passing the timeout token to ReadToEndAsync used
                // to discard the only useful Mihomo error before it could be classified.
                var standardOut = process.StandardOutput.ReadToEndAsync();
                var standardError = process.StandardError.ReadToEndAsync();
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { /* The validation process won the exit race. */ }
                    await process.WaitForExitAsync();
                    await Task.WhenAll(standardOut, standardError);
                    cancellationToken.ThrowIfCancellationRequested();
                    var problem = ClassifyConfigurationValidationFailure((await standardOut) + "\n" + (await standardError), timedOut: true);
                    await WriteWarningAsync(ConfigurationValidationDiagnostic(problem, timedOut: true), cancellationToken);
                    return problem;
                }

                await Task.WhenAll(standardOut, standardError);
                if (process.ExitCode == 0) return null;
                var failure = ClassifyConfigurationValidationFailure((await standardOut) + "\n" + (await standardError), timedOut: false);
                await WriteWarningAsync(ConfigurationValidationDiagnostic(failure, timedOut: false), cancellationToken);
                return failure;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            {
                await WriteWarningAsync("Mihomo configuration validation could not be started: " + exception.GetType().Name, cancellationToken);
                return ProxyProblemCodes.RuntimeHealthCheckFailed;
            }
        }
        finally { official.Gate.Release(); }
    }

    private static string ClassifyConfigurationValidationFailure(string output, bool timedOut)
    {
        var normalized = output.ToLowerInvariant();
        return normalized.Contains("can't initial geoip", StringComparison.Ordinal)
               || normalized.Contains("can't download mmdb", StringComparison.Ordinal)
               || normalized.Contains("geoip.metadb", StringComparison.Ordinal)
               || (normalized.Contains("geoip", StringComparison.Ordinal) && normalized.Contains("dns resolve failed", StringComparison.Ordinal))
            ? ProxyProblemCodes.GeodataUnavailable
            : timedOut ? ProxyProblemCodes.RuntimeHealthCheckFailed : ProxyProblemCodes.ConfigInvalid;
    }
    private static string ConfigurationValidationDiagnostic(string problemCode, bool timedOut) => problemCode == ProxyProblemCodes.GeodataUnavailable
        ? "Mihomo could not initialize GEO data. Check bundled/local database staging, integrity and read permissions in the managed data directory; this code does not establish a download failure."
        : timedOut
            ? "Mihomo configuration validation exceeded its 10-second limit."
            : "Mihomo rejected the downloaded subscription configuration.";

    private Task WriteWarningAsync(string message, CancellationToken ct) => diagnostics.WriteAsync("warning", message, ct);
}
