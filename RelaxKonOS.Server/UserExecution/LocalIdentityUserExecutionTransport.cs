using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.Files;
using RelaxKonOS.Server.Observability;

namespace RelaxKonOS.Server.UserExecution;

/// <summary>
/// Development backend: performs ordinary user operations in the Server's own process.
/// </summary>
/// <remarks>
/// This exists because a Windows workstation cannot host the effective-user boundary without a
/// LocalSystem service (a non-SYSTEM process cannot obtain a token for another local account — S4U
/// needs <c>SeTcbPrivilege</c>), while on Linux the same boundary is only a per-request dropped
/// worker. Neither is worth installing to debug on the machine the developer is already sitting at,
/// where the effective user <em>is</em> the account the Server runs as.
///
/// It is therefore allowed only while <see cref="ServerProcessIdentity.Matches"/> holds. The moment
/// the resolved identity is somebody else, in-process execution would run as the Server account —
/// exactly the fallback the effective-OS-user boundary forbids — so it refuses with
/// <see cref="UserExecutionProblemCode.IdentityNotExecutable"/> instead of degrading. The guard is
/// enforced here, at the transport, so every caller of <see cref="IUserExecutionTransport"/> is
/// covered by the same check rather than only the file endpoint.
/// </remarks>
public sealed class LocalIdentityUserExecutionTransport(LocalFileService direct,
    ILogger<LocalIdentityUserExecutionTransport> logger, ICorrelationContextAccessor? correlation = null,
    ISecurityAuditWriter? securityAudit = null, ObservabilityOptions? observability = null) : IUserExecutionTransport
{
    public async Task<UserExecutionResult> ExecuteAsync(UserExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var operationId = request.OperationId is { } id && id != Guid.Empty ? id : Guid.NewGuid();
        var ambient = correlation?.Current;
        request = request with
        {
            OperationId = operationId,
            Correlation = request.Correlation ?? (ambient is null
                ? CorrelationContext.Create(operationId, "user.execution")
                : new CorrelationContext(ambient.CorrelationId, operationId, "user.execution")),
            Version = UserExecutionProtocol.Version
        };
        if (!WriteSecurityAudit(request, null, ObservabilityOutcome.Started))
        {
            logger.LogError("User-execution request was not started because security audit persistence is unavailable. OperationId={OperationId}", operationId);
            return new(false, Error: "security audit is unavailable", ProblemCode: UserExecutionProblemCode.HelperUnavailable);
        }
        if (!ServerProcessIdentity.Matches(request.Identity))
            return Complete(request, new(false, Error: "local identity execution is limited to the Server's own account",
                ProblemCode: UserExecutionProblemCode.IdentityNotExecutable));
        if (!UserExecutionRequestPolicy.IsValid(request, terminal: false))
            return Complete(request, new(false, Error: "invalid user-execution request", ProblemCode: UserExecutionProblemCode.InvalidRequest));
        try
        {
            var value = await DirectUserExecutionOperations.ExecuteAsync(direct, request);
            var output = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value, RelaxKonOSJsonOptions.Default));
            return Complete(request, new(true, output));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = new UserExecutionResult(false, Error: "user-execution operation was cancelled",
                ProblemCode: UserExecutionProblemCode.Cancelled);
            // The accepted audit record must always have a terminal outcome, including when the HTTP
            // caller goes away while the operation is running.
            WriteSecurityAudit(request, cancelled, ObservabilityOutcome.Cancelled);
            Audit(request, cancelled);
            throw;
        }
        catch (Exception exception)
        {
            return Complete(request, Fail(request, exception));
        }
    }

    /// <summary>
    /// Mirrors the problem codes the Helper channel produces for the same failures, so a caller
    /// cannot tell the two backends apart by their error classification — only by the guard.
    /// </summary>
    private UserExecutionResult Fail(UserExecutionRequest request, Exception exception)
    {
        var (code, message) = exception switch
        {
            DirectUserExecutionOperations.ContentTooLargeException =>
                (UserExecutionProblemCode.ContentTooLarge, "user-execution content is too large"),
            UnauthorizedAccessException => (UserExecutionProblemCode.AccessDenied, "access denied"),
            DirectoryNotFoundException or FileNotFoundException => (UserExecutionProblemCode.NotFound, "path not found"),
            IOException => (UserExecutionProblemCode.Conflict, "file operation failed"),
            ArgumentException or FormatException => (UserExecutionProblemCode.InvalidRequest, "invalid file operation"),
            _ => (UserExecutionProblemCode.InternalError, "user-execution operation failed"),
        };
        // A failure is expected to be a permission or path problem rather than a bug in the Server.
        // Log the operation and the failure class without the caller-controlled path.
        logger.LogInformation("Local identity user execution failed. OperationId={OperationId} Operation={Operation} FailureType={FailureType} ProblemCode={ProblemCode}",
            request.OperationId, request.Operation, exception.GetType().Name, code);
        return new(false, Error: message, ProblemCode: code);
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
    /// The runtime log records only hashes of the identity and the resource, never the account name
    /// or a caller-controlled path. The backend is named so that a log reader can tell an in-process
    /// operation from one that really crossed the effective-user boundary.
    /// </summary>
    private void Audit(UserExecutionRequest request, UserExecutionResult result)
    {
        var identityHash = request.Identity is null ? "invalid" : Hash($"{request.Identity.Platform}:{request.Identity.StableIdentity}");
        var resource = string.Join('\n', new[] { request.Path, request.DestinationPath }
            .Where(value => !string.IsNullOrWhiteSpace(value))!);
        logger.LogInformation("User-execution operation completed. OperationId={OperationId} Operation={Operation} Backend={Backend} IdentityHash={IdentityHash} ResourceHash={ResourceHash} Success={Success} ProblemCode={ProblemCode}",
            request.OperationId, request.Operation, "local-identity", identityHash, resource.Length == 0 ? "none" : Hash(resource), result.Success, result.ProblemCode);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..16];
}
