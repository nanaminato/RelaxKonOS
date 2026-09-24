using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.UserExecution;

/// <summary>
/// Windows-only client for the LocalSystem Helper's dedicated user-execution pipe. The pipe ACL
/// and HMAC secret are shared with the privileged Helper transport, but the endpoint and contract
/// are distinct so normal user I/O can never be parsed as an administrator operation.
/// </summary>
public sealed class WindowsNamedPipeUserExecutionTransport(PrivilegedHelperOptions options,
    ILogger<WindowsNamedPipeUserExecutionTransport> logger) : IUserExecutionTransport
{
    public async Task<UserExecutionResult> ExecuteAsync(UserExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        request = request with
        {
            OperationId = request.OperationId is { } id && id != Guid.Empty ? id : Guid.NewGuid(),
            Version = UserExecutionProtocol.Version,
        };
        if (!OperatingSystem.IsWindows())
            return Complete(request, Unavailable(UserExecutionProblemCode.UnsupportedPlatform));
        if (string.IsNullOrWhiteSpace(options.PipeName) || !TryGetSecret(out var secret))
            return Complete(request, Unavailable(UserExecutionProblemCode.HelperUnavailable));

        using var setupDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        setupDeadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 120)));
        var pipeName = UserExecutionProtocol.WindowsPipeName(options.PipeName);
        var submitted = false;
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough);
            await pipe.ConnectAsync(setupDeadline.Token);
            var requestJson = JsonSerializer.SerializeToUtf8Bytes(request, RelaxKonOSJsonOptions.Default);
            if (requestJson.Length > UserExecutionProtocol.MaximumRequestBytes)
                return Complete(request, new(false, Error: "user-execution request is too large",
                    ProblemCode: UserExecutionProblemCode.ContentTooLarge));
            await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new PipeEnvelope(
                Convert.ToBase64String(requestJson), Sign(secret, requestJson))), setupDeadline.Token);
            submitted = true;
            // Once submitted, cancellation of the HTTP request cannot make an in-flight mutation
            // disappear. Wait for the Helper's own bounded, authoritative result instead.
            using var responseDeadline = new CancellationTokenSource(
                TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds + 5, 6, 125)));
            var response = JsonSerializer.Deserialize<PipeEnvelope>(await ReadFrameAsync(pipe, responseDeadline.Token));
            if (response is null || !TryDecodeAndVerify(secret, response, out var payload))
                return Complete(request, InvalidProtocol());
            var result = JsonSerializer.Deserialize<UserExecutionResult>(payload, RelaxKonOSJsonOptions.Default);
            return Complete(request, result is null || result.Version != UserExecutionProtocol.Version
                || !Enum.IsDefined(result.ProblemCode) ? InvalidProtocol() : result);
        }
        catch (OperationCanceledException) when (submitted || !cancellationToken.IsCancellationRequested)
        {
            return Complete(request, new(false, Error: "user-execution Helper timed out",
                ProblemCode: UserExecutionProblemCode.TimedOut));
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception,
                "Could not communicate with the Windows user-execution Helper. PipeName={PipeName}", pipeName);
            return Complete(request, Unavailable(UserExecutionProblemCode.HelperUnavailable));
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
        if (payload.Length is < 1 or > UserExecutionProtocol.MaximumAuthenticatedPipeFrameBytes)
            throw new InvalidDataException("invalid user-execution pipe request size");
        await stream.WriteAsync(BitConverter.GetBytes(payload.Length), cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken);
        var length = BitConverter.ToInt32(header);
        if (length is < 1 or > UserExecutionProtocol.MaximumAuthenticatedPipeFrameBytes)
            throw new InvalidDataException("invalid user-execution pipe response size");
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

    private static string Sign(byte[] secret, byte[] payload)
        => Convert.ToBase64String(HMACSHA256.HashData(secret, payload));

    private static bool TryDecodeAndVerify(byte[] secret, PipeEnvelope envelope, out byte[] payload)
    {
        payload = [];
        try
        {
            payload = Convert.FromBase64String(envelope.PayloadBase64);
            return payload.Length <= UserExecutionProtocol.MaximumResponseBytes
                && CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(envelope.SignatureBase64),
                    HMACSHA256.HashData(secret, payload));
        }
        catch (FormatException) { return false; }
    }

    private UserExecutionResult Complete(UserExecutionRequest request, UserExecutionResult result)
    {
        logger.LogInformation(
            "User execution completed. OperationId={OperationId} Operation={Operation} IdentityHash={IdentityHash} ResourceHash={ResourceHash} Success={Success} Problem={Problem}",
            request.OperationId, request.Operation, Hash(request.Identity.StableIdentity), Hash(request.Path ?? request.DestinationPath),
            result.Success, result.ProblemCode);
        return result;
    }

    private static string Hash(string? value) => string.IsNullOrEmpty(value) ? "none"
        : Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..16];
    private static UserExecutionResult Unavailable(UserExecutionProblemCode code)
        => new(false, Error: "Windows user execution is unavailable", ProblemCode: code);
    private static UserExecutionResult InvalidProtocol()
        => new(false, Error: "invalid user-execution Helper response", ProblemCode: UserExecutionProblemCode.InvalidProtocol);
    private sealed record PipeEnvelope(string PayloadBase64, string SignatureBase64);
}
