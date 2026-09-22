using System.Diagnostics;
using System.Text.Json;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.UserExecution;

/// <summary>Starts the installed one-shot Helper in its dedicated user-execution mode.</summary>
public sealed class LinuxUserExecutionTransport(PrivilegedHelperOptions options, ILogger<LinuxUserExecutionTransport> logger) : IUserExecutionTransport
{
    public async Task<UserExecutionResult> ExecuteAsync(UserExecutionRequest request, CancellationToken cancellationToken = default)
    {
        request = request with { OperationId = request.OperationId is { } id && id != Guid.Empty ? id : Guid.NewGuid(), Version = UserExecutionProtocol.Version };
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(options.HelperPath) || !File.Exists(options.HelperPath))
            return new(false, Error: "the Linux user-execution Helper is unavailable", ProblemCode: UserExecutionProblemCode.HelperUnavailable);
        var start = new ProcessStartInfo(options.SudoPath);
        start.ArgumentList.Add("-n"); start.ArgumentList.Add(options.HelperPath); start.ArgumentList.Add("--user-execution");
        TrustedProcessEnvironment.Apply(start);
        start.RedirectStandardInput = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        start.UseShellExecute = false; start.CreateNoWindow = true;
        try
        {
            using var process = Process.Start(start);
            if (process is null) return new(false, Error: "user-execution Helper could not be started", ProblemCode: UserExecutionProblemCode.HelperUnavailable);
            await JsonSerializer.SerializeAsync(process.StandardInput.BaseStream, request, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default, CancellationToken.None);
            await process.StandardInput.DisposeAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(output, error, process.WaitForExitAsync(CancellationToken.None));
            if (!string.IsNullOrWhiteSpace(await error)) logger.LogWarning("User-execution Helper emitted diagnostics. OperationId={OperationId}", request.OperationId);
            return JsonSerializer.Deserialize<UserExecutionResult>(await output, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default)
                ?? new(false, Error: "user-execution Helper returned no result", ProblemCode: UserExecutionProblemCode.InternalError);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            logger.LogWarning(exception, "User-execution Helper transport failed. OperationId={OperationId}", request.OperationId);
            return new(false, Error: "user-execution Helper transport failed", ProblemCode: UserExecutionProblemCode.HelperUnavailable);
        }
    }
}
