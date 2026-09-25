using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.Observability;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.UserExecution;

/// <summary>Starts the installed one-shot Helper in its dedicated user-execution mode.</summary>
public sealed class LinuxUserExecutionTransport(PrivilegedHelperOptions options, ILogger<LinuxUserExecutionTransport> logger,
    ICorrelationContextAccessor? correlation = null, ISecurityAuditWriter? securityAudit = null,
    ObservabilityOptions? observability = null) : IUserExecutionTransport
{
    private const string DiagnosticPrefix = "relaxkonos-diagnostic:";
    // Only these Helper-emitted diagnostics may reach the runtime log. Anything else is discarded
    // instead of being written as un-sanitized external process output.
    private static readonly HashSet<string> KnownDiagnostics = new(StringComparer.Ordinal)
    {
        "user-execution-identity-mismatch", "user-execution-identity-transition-failed"
    };

    public async Task<UserExecutionResult> ExecuteAsync(UserExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var operationId = request.OperationId is { } id && id != Guid.Empty ? id : Guid.NewGuid();
        var ambient = correlation?.Current;
        request = request with
        {
            OperationId = operationId,
            // The Helper re-establishes its own local scope from this metadata. Only safe
            // correlation fields cross the process boundary; never a credential or account name.
            Correlation = request.Correlation ?? (ambient is null
                ? CorrelationContext.Create(operationId, "user.execution")
                : new CorrelationContext(ambient.CorrelationId, operationId, "user.execution")),
            Version = UserExecutionProtocol.Version
        };
        if (!WriteSecurityAudit(request, null, ObservabilityOutcome.Started))
        {
            logger.LogError("User-execution Helper operation was not started because security audit persistence is unavailable. Operation={Operation}", request.Operation);
            return new(false, Error: "security audit is unavailable", ProblemCode: UserExecutionProblemCode.HelperUnavailable);
        }
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(options.HelperPath) || !File.Exists(options.HelperPath))
            return Complete(request, new(false, Error: "the Linux user-execution Helper is unavailable", ProblemCode: UserExecutionProblemCode.HelperUnavailable));
        var start = new ProcessStartInfo(options.SudoPath);
        start.ArgumentList.Add("-n"); start.ArgumentList.Add(options.HelperPath); start.ArgumentList.Add("--user-execution");
        TrustedProcessEnvironment.Apply(start);
        start.RedirectStandardInput = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        start.UseShellExecute = false; start.CreateNoWindow = true;
        try
        {
            using var process = Process.Start(start);
            if (process is null) return Complete(request, new(false, Error: "user-execution Helper could not be started", ProblemCode: UserExecutionProblemCode.HelperUnavailable));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 600)));
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await JsonSerializer.SerializeAsync(process.StandardInput.BaseStream, request,
                    RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default, execution.Token);
                await process.StandardInput.DisposeAsync();
                var output = ReadBoundedTextAsync(process.StandardOutput.BaseStream,
                    UserExecutionProtocol.MaximumResponseBytes, execution.Token);
                var error = DrainDiagnosticsAsync(process.StandardError.BaseStream, execution.Token);
                await Task.WhenAll(output, error, process.WaitForExitAsync(execution.Token));
                if (await error) logger.LogWarning("User-execution Helper emitted diagnostics. OperationId={OperationId}", request.OperationId);
                return Complete(request, JsonSerializer.Deserialize<UserExecutionResult>(await output, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default)
                    ?? new(false, Error: "user-execution Helper returned no result", ProblemCode: UserExecutionProblemCode.InternalError));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Kill(process);
                var cancelled = new UserExecutionResult(false, Error: "user-execution operation was cancelled",
                    ProblemCode: UserExecutionProblemCode.Cancelled);
                // The accepted audit record must always have a terminal outcome, including when
                // the HTTP caller cancels while the Helper is running.
                WriteSecurityAudit(request, cancelled, ObservabilityOutcome.Cancelled);
                Audit(request, cancelled);
                throw;
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                logger.LogWarning("User-execution Helper timed out. OperationId={OperationId}", request.OperationId);
                return Complete(request, new(false, Error: "user-execution operation timed out", ProblemCode: UserExecutionProblemCode.TimedOut));
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "User-execution Helper transport failed. OperationId={OperationId}", request.OperationId);
            return Complete(request, new(false, Error: "user-execution Helper transport failed", ProblemCode: UserExecutionProblemCode.HelperUnavailable));
        }
    }

    private UserExecutionResult Complete(UserExecutionRequest request, UserExecutionResult result)
    {
        WriteSecurityAudit(request, result, result.Success ? ObservabilityOutcome.Succeeded : ObservabilityOutcome.Failed);
        Audit(request, result);
        return result;
    }

    private bool WriteSecurityAudit(UserExecutionRequest request, UserExecutionResult? result, ObservabilityOutcome outcome)
    {
        if (securityAudit is null) return true;
        var context = request.Correlation!;
        return securityAudit.TryWriteAsync(new SecurityAuditEvent(
            outcome == ObservabilityOutcome.Started ? ObservabilityEventCatalog.UserExecutionRequestAccepted.Id : ObservabilityEventCatalog.UserExecutionRequestCompleted.Id,
            outcome == ObservabilityOutcome.Started ? ObservabilityEventCatalog.UserExecutionRequestAccepted.Name : ObservabilityEventCatalog.UserExecutionRequestCompleted.Name,
            outcome, "server", context.CorrelationId, DateTimeOffset.UtcNow, observability?.InstanceId ?? "unconfigured",
            "user.execution", request.OperationId, ActorReference: request.Identity.CanonicalAccount,
            ResourceType: "user-execution-operation", ResourceReference: request.Path ?? request.DestinationPath,
            ProblemCode: result?.ProblemCode.ToString())).GetAwaiter().GetResult();
    }

    /// <summary>
    /// The runtime log records only hashes of the identity and the resource, never the account
    /// name or a caller-controlled path.
    /// </summary>
    private void Audit(UserExecutionRequest request, UserExecutionResult result)
    {
        var identityHash = request.Identity is null ? "invalid" : Hash($"{request.Identity.Platform}:{request.Identity.StableIdentity}");
        var resource = string.Join('\n', new[] { request.Path, request.DestinationPath }
            .Where(value => !string.IsNullOrWhiteSpace(value))!);
        logger.LogInformation("User-execution operation completed. OperationId={OperationId} Operation={Operation} IdentityHash={IdentityHash} ResourceHash={ResourceHash} Success={Success} ProblemCode={ProblemCode}",
            request.OperationId, request.Operation, identityHash, resource.Length == 0 ? "none" : Hash(resource), result.Success, result.ProblemCode);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    private static async Task<string> ReadBoundedTextAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var content = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var tooLarge = false;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (content.Length + read <= maximumBytes)
                await content.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            else
                tooLarge = true;
        }
        if (tooLarge) throw new InvalidDataException("User-execution Helper response is too large.");
        return Encoding.UTF8.GetString(content.GetBuffer(), 0, checked((int)content.Length));
    }

    /// <summary>
    /// Always drains stderr so the Helper can exit, but never returns its content: the structured,
    /// versioned result on stdout is the only channel the Server reads.
    /// </summary>
    private static async Task<bool> DrainDiagnosticsAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var emitted = false;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) return emitted;
            emitted = true;
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
    }
}
