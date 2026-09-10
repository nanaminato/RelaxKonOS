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
        using (process)
        {
            // Once stdin begins carrying a typed mutation, completion is authoritative. A
            // disconnected HTTP client must not make the Server report a cancelled operation
            // while the root-owned Helper is still applying or rolling back a transaction.
            await JsonSerializer.SerializeAsync(process.StandardInput.BaseStream, request, cancellationToken: CancellationToken.None);
            await process.StandardInput.DisposeAsync();
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = new CancellationTokenSource();
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
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
