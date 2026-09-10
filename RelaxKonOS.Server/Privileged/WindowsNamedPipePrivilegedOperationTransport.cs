using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.Privileged;

/// <summary>
/// Windows-only client for the LocalSystem Helper service. This intentionally never starts an
/// executable: missing, unauthenticated, or incompatible Helper services fail closed.
/// </summary>
public sealed class WindowsNamedPipePrivilegedOperationTransport(PrivilegedHelperOptions options,
    ILogger<WindowsNamedPipePrivilegedOperationTransport> logger) : IPrivilegedOperationTransport
{
    public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
    {
        request = request with { OperationId = request.OperationId is { } id && id != Guid.Empty ? id : Guid.NewGuid(), Version = PrivilegedOperationProtocol.Version };
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(options.PipeName) || !TryGetSecret(out var secret))
            return Complete(request, Unavailable("privileged helper service is not configured"));
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
            using var responseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 120)));
            var responseBytes = await ReadFrameAsync(pipe, responseTimeout.Token);
            var response = JsonSerializer.Deserialize<PipeEnvelope>(responseBytes);
            if (response is null || !TryDecodeAndVerify(secret, response, out var payload))
            {
                logger.LogWarning("Privileged Helper pipe response did not pass authentication.");
                return Complete(request, Unavailable("privileged helper service authentication failed"));
            }
            var result = JsonSerializer.Deserialize<PrivilegedOperationResult>(payload)
                ?? Unavailable("privileged helper service returned no result");
            return Complete(request, result);
        }
        catch (OperationCanceledException)
        {
            return Complete(request, new(false, 124, Error: "privileged helper service timed out", ProblemCode: PrivilegedProblemCode.TimedOut));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception, "Could not communicate with the local privileged Helper service.");
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
    private sealed record PipeEnvelope(string PayloadBase64, string SignatureBase64);
}
