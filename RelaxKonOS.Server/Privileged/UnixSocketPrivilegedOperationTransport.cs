using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Observability;

namespace RelaxKonOS.Server.Privileged;

/// <summary>
/// Linux client for the persistent root Helper. One request owns one Unix Domain Socket connection:
/// handshake, one framed request, streamed progress frames, one terminal result. The Server never
/// elevates a child process and never starts <c>sudo</c> on this path.
/// </summary>
public sealed class UnixSocketPrivilegedOperationTransport(PrivilegedHelperOptions options,
    ILogger<UnixSocketPrivilegedOperationTransport> logger, ICorrelationContextAccessor? correlation = null,
    ISecurityAuditWriter? securityAudit = null, ObservabilityOptions? observability = null, IHttpContextAccessor? http = null)
    : IPrivilegedOperationTransport
{
    public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
    {
        var operationId = request.OperationId is { } requestedId && requestedId != Guid.Empty ? requestedId : Guid.NewGuid();
        var ambient = correlation?.Current;
        request = request with
        {
            OperationId = operationId,
            Correlation = request.Correlation ?? (ambient is null
                ? CorrelationContext.Create(operationId, "privileged.operation")
                : new CorrelationContext(ambient.CorrelationId, operationId, "privileged.operation")),
            Version = PrivilegedOperationProtocol.Version
        };
        if (!WriteSecurityAudit(request, null, ObservabilityOutcome.Started))
            return new(false, 69, Error: "security audit is unavailable", ProblemCode: PrivilegedProblemCode.HelperUnavailable);
        if (string.IsNullOrWhiteSpace(options.SocketPath) || options.SocketPath.Length > 100)
        {
            // The socket path is a fixed local runtime path, never a network endpoint. Log only
            // whether it was configured; the value itself is installation metadata.
            logger.LogWarning("Linux privileged Helper socket path is not configured.");
            return Complete(request, Unavailable("privileged helper service is not configured"));
        }

        // Cancellation stays meaningful while connecting and handshaking. Once the request frame is
        // sent, the Helper owns the authoritative operation deadline; disconnecting cannot roll back
        // an already-started privileged mutation.
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 120)));
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(options.SocketPath), connectTimeout.Token);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            await PrivilegedHelperSocketFraming.WriteAsync(stream,
                JsonSerializer.SerializeToUtf8Bytes(PrivilegedHelperHandshake.Hello()), connectTimeout.Token);
            var welcome = await ReadHandshakeAsync(stream, connectTimeout.Token);
            if (welcome is null || !welcome.IsValid(PrivilegedHelperHandshake.Hello())
                || welcome.Type != PrivilegedHelperHandshake.WelcomeType)
            {
                if (welcome is { Type: PrivilegedHelperHandshake.RejectedType, ProblemCode: { } rejection })
                    return Complete(request, new(false, 69, Error: welcome.Error, ProblemCode: rejection));
                return Complete(request, new(false, 65, ProblemCode: PrivilegedProblemCode.InvalidProtocol));
            }

            await PrivilegedHelperSocketFraming.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(request), CancellationToken.None);
            using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (ResponseTimeout(request.Operation) is { } timeout) responseTimeout.CancelAfter(timeout);
            for (var count = 0; count < PrivilegedOperationFrame.MaximumFrames; count++)
            {
                var payload = await PrivilegedHelperSocketFraming.ReadAsync(stream,
                    PrivilegedHelperTransportProtocol.MaximumFrameBytes, responseTimeout.Token);
                if (payload is null) return Complete(request, new(false, 65, ProblemCode: PrivilegedProblemCode.InvalidProtocol));
                var frame = JsonSerializer.Deserialize<PrivilegedOperationFrame>(payload);
                if (frame is null || !frame.IsValid() || frame.Type == "progress" && payload.Length > PrivilegedOperationFrame.MaximumProgressFrameBytes)
                    return Complete(request, new(false, 65, ProblemCode: PrivilegedProblemCode.InvalidProtocol));
                if (frame.Result is { } result) return Complete(request, result);
                await PrivilegedFrameReader.ReportAsync(frame);
            }
            return Complete(request, new(false, 65, ProblemCode: PrivilegedProblemCode.InvalidProtocol));
        }
        catch (OperationCanceledException)
        {
            return Complete(request, new(false, 124, Error: "privileged helper service timed out",
                ProblemCode: PrivilegedProblemCode.TimedOut));
        }
        catch (Exception exception) when (exception is IOException or SocketException or UnauthorizedAccessException
            or JsonException or InvalidDataException or ArgumentException)
        {
            // The common cause on a fresh host is simply that the unit is not installed or started.
            // Keep the failure class visible without logging a payload.
            logger.LogWarning("Could not communicate with the local privileged Helper socket. FailureType={FailureType}",
                exception.GetType().Name);
            return Complete(request, Unavailable("privileged helper service is unavailable"));
        }
    }

    /// <summary>
    /// Most operations own their own bounded Helper-side deadline. The few long conversational
    /// operations keep the same client-side caps the Windows transport uses so a wedged Helper
    /// cannot hold a request open forever.
    /// </summary>
    private TimeSpan? ResponseTimeout(PrivilegedOperationKind operation) => operation switch
    {
        PrivilegedOperationKind.TailscaleStatus or PrivilegedOperationKind.TailscaleInstallerQuery => TimeSpan.FromSeconds(15),
        PrivilegedOperationKind.TailscaleControlRead or PrivilegedOperationKind.TailscaleControlReceipt
            or PrivilegedOperationKind.TailscaleRecoveryRead or PrivilegedOperationKind.TailscaleRecoveryReceipt
            or PrivilegedOperationKind.TailscaleLoginBegin or PrivilegedOperationKind.TailscaleLoginObserve
            or PrivilegedOperationKind.TailscaleLoginReceipt or PrivilegedOperationKind.TailscaleLoginChallenge
            or PrivilegedOperationKind.TailscaleLoginRecoveryRead or PrivilegedOperationKind.TailscaleLoginClose
            or PrivilegedOperationKind.TailscaleLoginRecoveryReceipt => TimeSpan.FromSeconds(50),
        PrivilegedOperationKind.ProcessSnapshot or PrivilegedOperationKind.ProcessIcon
            or PrivilegedOperationKind.ProcessTerminate => TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 120)),
        // Package transactions own a long per-step Helper deadline. Keep a wider outer bound so a
        // wedged Helper process still produces a stable timeout instead of an unbounded wait.
        _ => TimeSpan.FromSeconds(Math.Clamp(options.PackageOperationTimeoutSeconds, 60, 7200))
    };

    private static async Task<PrivilegedHelperHandshake?> ReadHandshakeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var payload = await PrivilegedHelperSocketFraming.ReadAsync(stream,
            PrivilegedHelperTransportProtocol.MaximumHandshakeFrameBytes, cancellationToken);
        return payload is null ? null : JsonSerializer.Deserialize<PrivilegedHelperHandshake>(payload);
    }

    private static PrivilegedOperationResult Unavailable(string detail) =>
        new(false, 69, Error: detail, ProblemCode: PrivilegedProblemCode.HelperUnavailable);

    private PrivilegedOperationResult Complete(PrivilegedOperationRequest request, PrivilegedOperationResult result)
    {
        WriteSecurityAudit(request, result, result.Success ? ObservabilityOutcome.Succeeded : ObservabilityOutcome.Failed);
        AuditRuntime(request, result);
        return result;
    }

    private bool WriteSecurityAudit(PrivilegedOperationRequest request, PrivilegedOperationResult? result, ObservabilityOutcome outcome)
    {
        if (securityAudit is null) return true;
        var context = request.Correlation!;
        // The audit record names the resource class and a digest, never the path itself.
        var fileSource = request.FileAuthorizationSource;
        var resource = string.Join("\n", new[] { request.Path, request.DestinationPath }
            .Where(value => !string.IsNullOrWhiteSpace(value))!);
        var reference = fileSource is null ? request.Path ?? request.ServiceId ?? request.DestinationPath
            : resource.Length == 0 ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(resource)))[..16];
        var actor = PrivilegedAuditActorScope.Current
            ?? http?.HttpContext?.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        return securityAudit.TryWriteAsync(new SecurityAuditEvent(
            outcome == ObservabilityOutcome.Started ? ObservabilityEventCatalog.PrivilegedRequestAccepted.Id : ObservabilityEventCatalog.PrivilegedRequestCompleted.Id,
            outcome == ObservabilityOutcome.Started ? ObservabilityEventCatalog.PrivilegedRequestAccepted.Name : ObservabilityEventCatalog.PrivilegedRequestCompleted.Name,
            outcome, "server", context.CorrelationId, DateTimeOffset.UtcNow, observability?.InstanceId ?? "unconfigured",
            fileSource is null ? "privileged.operation" : $"privileged.file.{fileSource}.{request.Operation}", request.OperationId,
            ActorReference: actor, ResourceType: "privileged-operation", ResourceReference: reference,
            ProblemCode: result?.ProblemCode.ToString())).GetAwaiter().GetResult();
    }

    private void AuditRuntime(PrivilegedOperationRequest request, PrivilegedOperationResult result)
    {
        var resource = string.Join("\n", new[] { request.Path, request.DestinationPath, request.ServiceId, request.EnvironmentTarget?.ResourceId }
            .Where(value => !string.IsNullOrWhiteSpace(value))!);
        var resourceHash = resource.Length == 0 ? "none" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resource)))[..16];
        logger.LogInformation("Privileged Helper socket operation completed. OperationId={OperationId} Operation={Operation} "
            + "ResourceHash={ResourceHash} Success={Success} ProblemCode={ProblemCode}",
            request.OperationId, request.Operation, resourceHash, result.Success, result.ProblemCode);
    }
}
