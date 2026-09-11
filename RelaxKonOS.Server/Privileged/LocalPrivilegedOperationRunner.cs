using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.Privileged;

/// <summary>Runs the installed helper. Linux uses its dedicated passwordless sudoers rule.</summary>
public sealed class LocalPrivilegedOperationRunner(PrivilegedHelperOptions options, ILogger<LocalPrivilegedOperationRunner> logger) : IPrivilegedOperationTransport
{
    public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
    {
        request = request with { OperationId = request.OperationId is { } id && id != Guid.Empty ? id : Guid.NewGuid(), Version = PrivilegedOperationProtocol.Version };
        if (!OperatingSystem.IsLinux())
            return Complete(request, new(false, 69, Error: "the Linux privileged transport is unavailable on this platform", ProblemCode: PrivilegedProblemCode.HelperUnavailable));
        if (string.IsNullOrWhiteSpace(options.HelperPath) || !File.Exists(options.HelperPath))
            return Complete(request, new(false, 69, Error: "privileged helper is not installed", ProblemCode: PrivilegedProblemCode.HelperUnavailable));

        var start = new ProcessStartInfo(options.SudoPath) { ArgumentList = { "-n", options.HelperPath } };
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;

        Process? process;
        try { process = Process.Start(start); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not start the privileged helper.");
            return Complete(request, new(false, 69, Error: "privileged helper could not be started", ProblemCode: PrivilegedProblemCode.HelperUnavailable));
        }
        if (process is null) return Complete(request, new(false, 69, Error: "privileged helper could not be started", ProblemCode: PrivilegedProblemCode.HelperUnavailable));
        var handoffProcessOwnership = false;
        try
        {
            // Once stdin begins carrying a typed mutation, completion is authoritative. A
            // disconnected HTTP client must not make the Server report a cancelled operation
            // while the root-owned Helper is still applying or rolling back a transaction.
            await JsonSerializer.SerializeAsync(process.StandardInput.BaseStream, request, cancellationToken: CancellationToken.None);
            await process.StandardInput.DisposeAsync();
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = new CancellationTokenSource();
            timeout.CancelAfter(TimeoutFor(request.Operation));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                // sudo runs the Helper as root. The Server intentionally does not have permission
                // to terminate that process tree, and attempting to do so used to turn a timeout
                // into an unhandled 500. Each expensive Helper operation has its own deadline, so
                // keep the pipes open while it reaches that bounded outcome and report this request
                // as timed out without claiming that the root operation was cancelled.
                handoffProcessOwnership = true;
                _ = ObserveTimedOutHelperAsync(process, output, error, request);
                logger.LogWarning("Privileged Helper operation exceeded its Server wait timeout. OperationId={OperationId}; the Helper will continue to its own bounded completion.", request.OperationId);
                return Complete(request, new(false, 124, Error: "privileged helper timed out", ProblemCode: PrivilegedProblemCode.TimedOut));
            }

            var response = await output;
            var stderr = await error;
            try
            {
                var result = JsonSerializer.Deserialize<PrivilegedOperationResult>(response)
                    ?? new(false, process.ExitCode, Error: "privileged helper returned no result", ProblemCode: PrivilegedProblemCode.HelperUnavailable);
                return Complete(request, result);
            }
            catch (JsonException)
            {
                // A sudo rejection or a damaged apphost writes no protocol JSON. It is a helper
                // availability problem, not a file I/O failure. Keep stderr out of the HTTP response.
                logger.LogWarning("Privileged helper returned invalid output. ExitCode={ExitCode}; Stderr={Stderr}",
                    process.ExitCode, string.IsNullOrWhiteSpace(stderr) ? "(empty)" : stderr);
                return Complete(request, new(false, 69, Error: "privileged helper failed; check the Server logs and sudoers configuration", ProblemCode: PrivilegedProblemCode.HelperUnavailable));
            }
        }
        finally
        {
            if (!handoffProcessOwnership) process.Dispose();
        }
    }

    private TimeSpan TimeoutFor(PrivilegedOperationKind operation)
    {
        var seconds = operation is PrivilegedOperationKind.SmbPackageInstall or PrivilegedOperationKind.NginxPackageInstall
            or PrivilegedOperationKind.NginxPackageUninstall or PrivilegedOperationKind.GitPackageInstall
            ? Math.Max(options.TimeoutSeconds, options.PackageOperationTimeoutSeconds)
            : options.TimeoutSeconds;
        return TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    private async Task ObserveTimedOutHelperAsync(Process process, Task<string> output, Task<string> error, PrivilegedOperationRequest request)
    {
        try
        {
            await process.WaitForExitAsync();
            var stderr = await error;
            var response = await output;
            logger.LogInformation("Timed-out privileged Helper operation finished. OperationId={OperationId}; ExitCode={ExitCode}; ResponseReceived={ResponseReceived}; Stderr={Stderr}",
                request.OperationId, process.ExitCode, !string.IsNullOrWhiteSpace(response), string.IsNullOrWhiteSpace(stderr) ? "(empty)" : stderr);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not observe the timed-out privileged Helper operation. OperationId={OperationId}", request.OperationId);
        }
        finally { process.Dispose(); }
    }

    private PrivilegedOperationResult Complete(PrivilegedOperationRequest request, PrivilegedOperationResult result)
    {
        Audit(request, result);
        return result;
    }

    private void Audit(PrivilegedOperationRequest request, PrivilegedOperationResult result)
    {
        var resource = string.Join("\n", new[] { request.Path, request.DestinationPath, request.ServiceId }.Where(value => !string.IsNullOrWhiteSpace(value))!);
        var resourceHash = resource.Length == 0 ? "none" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resource)))[..16];
        logger.LogInformation("Privileged Helper operation completed. OperationId={OperationId} Operation={Operation} ResourceHash={ResourceHash} Success={Success} ProblemCode={ProblemCode}",
            request.OperationId, request.Operation, resourceHash, result.Success, result.ProblemCode);
    }
}
