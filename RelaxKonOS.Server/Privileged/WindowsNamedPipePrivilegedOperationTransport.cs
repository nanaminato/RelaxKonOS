using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Server.Observability;

namespace RelaxKonOS.Server.Privileged;

/// <summary>
/// Windows-only client for the LocalSystem Helper service. This intentionally never starts an
/// executable: missing, unauthenticated, or incompatible Helper services fail closed.
/// </summary>
public sealed class WindowsNamedPipePrivilegedOperationTransport(PrivilegedHelperOptions options,
    ILogger<WindowsNamedPipePrivilegedOperationTransport> logger, ICorrelationContextAccessor? correlation = null,
    ISecurityAuditWriter? securityAudit = null, ObservabilityOptions? observability = null) : IPrivilegedOperationTransport
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
        if (!OperatingSystem.IsWindows())
            return Complete(request, Unavailable("the Windows privileged helper transport is unavailable on this platform"));
        if (string.IsNullOrWhiteSpace(options.PipeName) || !TryGetSecret(out var secret))
        {
            // The pipe name is not a credential and is vital when a developer console Helper is
            // running. Never log the HMAC secret itself.
            logger.LogWarning("Windows privileged Helper configuration is incomplete. PipeName={PipeName} HasSharedSecret={HasSharedSecret}",
                options.PipeName, !string.IsNullOrWhiteSpace(options.SharedSecret));
            return Complete(request, Unavailable("privileged helper service is not configured"));
        }
        // Cancellation remains meaningful while opening the local pipe. Once the signed frame
        // is sent, wait for the Helper's authoritative response with its own bounded timeout.
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 120)));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
            await pipe.ConnectAsync(connectTimeout.Token);
            var requestJson = JsonSerializer.SerializeToUtf8Bytes(request);
            var signed = new PipeEnvelope(Convert.ToBase64String(requestJson), Sign(secret, requestJson));
            await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(signed), CancellationToken.None);
            for (var count = 0; count < PrivilegedOperationFrame.MaximumFrames; count++)
            {
                // A submitted LocalSystem operation remains authoritative until its own deadline.
                var responseBytes = await ReadFrameAsync(pipe, CancellationToken.None);
                var response = JsonSerializer.Deserialize<PipeEnvelope>(responseBytes);
                if (response is null || !TryDecodeAndVerify(secret, response, out var payload))
                    return Complete(request, new(false, 65, ProblemCode: PrivilegedProblemCode.InvalidProtocol));
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
            return Complete(request, new(false, 124, Error: "privileged helper service timed out", ProblemCode: PrivilegedProblemCode.TimedOut));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // In a Windows development session the most common cause is that the Server's
            // launch profile did not supply the console Helper's pipe name and HMAC secret.
            // Keep the secret out of logs, but make the selected pipe and failure class visible.
            logger.LogWarning("Could not communicate with the local privileged Helper service. FailureType={FailureType}", exception.GetType().Name);
            return Complete(request, Unavailable("privileged helper service is unavailable"));
        }
    }

    private bool TryGetSecret(out byte[] secret)
    {
        secret = [];
        try
        {
            secret = Convert.FromBase64String(options.SharedSecret);
            return secret.Length >= 32;
        }
        catch (FormatException) { return false; }
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length > PrivilegedOperationProtocol.MaximumRequestBytes) throw new InvalidDataException("pipe request too large");
        var header = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken);
        var length = BitConverter.ToInt32(header);
        if (length is < 1 or > PrivilegedOperationProtocol.MaximumRequestBytes) throw new InvalidDataException("invalid pipe response size");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return payload;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (!buffer.IsEmpty)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            buffer = buffer[read..];
        }
    }

    private static string Sign(byte[] secret, byte[] payload) => Convert.ToBase64String(HMACSHA256.HashData(secret, payload));
    private static bool TryDecodeAndVerify(byte[] secret, PipeEnvelope envelope, out byte[] payload)
    {
        payload = [];
        try
        {
            payload = Convert.FromBase64String(envelope.PayloadBase64);
            var signature = Convert.FromBase64String(envelope.SignatureBase64);
            return CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(secret, payload));
        }
        catch (FormatException) { return false; }
    }

    private static PrivilegedOperationResult Unavailable(string detail) => new(false, 69, Error: detail, ProblemCode: PrivilegedProblemCode.HelperUnavailable);

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
        return securityAudit.TryWriteAsync(new SecurityAuditEvent(
            outcome == ObservabilityOutcome.Started ? ObservabilityEventCatalog.PrivilegedRequestAccepted.Id : ObservabilityEventCatalog.PrivilegedRequestCompleted.Id,
            outcome == ObservabilityOutcome.Started ? ObservabilityEventCatalog.PrivilegedRequestAccepted.Name : ObservabilityEventCatalog.PrivilegedRequestCompleted.Name,
            outcome, "server", context.CorrelationId, DateTimeOffset.UtcNow, observability?.InstanceId ?? "unconfigured",
            "privileged.operation", request.OperationId, ResourceType: "privileged-operation", ResourceReference: request.Path ?? request.ServiceId ?? request.DestinationPath,
            ProblemCode: result?.ProblemCode.ToString())).GetAwaiter().GetResult();
    }

    private void AuditRuntime(PrivilegedOperationRequest request, PrivilegedOperationResult result)
    {
        var resource = string.Join("\n", new[] { request.Path, request.DestinationPath, request.ServiceId, request.EnvironmentTarget?.ResourceId }.Where(value => !string.IsNullOrWhiteSpace(value))!);
        var resourceHash = resource.Length == 0 ? "none" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resource)))[..16];
        logger.LogInformation("Privileged Helper operation completed. OperationId={OperationId} Operation={Operation} ResourceHash={ResourceHash} Success={Success} ProblemCode={ProblemCode}",
            request.OperationId, request.Operation, resourceHash, result.Success, result.ProblemCode);
    }
    private sealed record PipeEnvelope(string PayloadBase64, string SignatureBase64);
}
