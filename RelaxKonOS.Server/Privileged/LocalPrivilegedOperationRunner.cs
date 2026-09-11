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
        catch (Exception)
        {
            logger.LogWarning("Could not start the privileged helper.");
            return Complete(request, new(false, 69, Error: "privileged helper could not be started", ProblemCode: PrivilegedProblemCode.HelperUnavailable));
        }
        if (process is null) return Complete(request, new(false, 69, Error: "privileged helper could not be started", ProblemCode: PrivilegedProblemCode.HelperUnavailable));
        using (process)
        {
            await JsonSerializer.SerializeAsync(process.StandardInput.BaseStream, request, cancellationToken: CancellationToken.None);
            await process.StandardInput.DisposeAsync();
            // Read bounded protocol frames and drain stderr concurrently. Hold installation locks
            // until the root worker actually exits, including after a Server wait deadline.
            var output = PrivilegedFrameReader.ReadAsync(process.StandardOutput.BaseStream);
            var error = PrivilegedFrameReader.DrainAsync(process.StandardError.BaseStream);
            await Task.WhenAll(output, error, process.WaitForExitAsync(CancellationToken.None));
            return Complete(request, await output);
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
