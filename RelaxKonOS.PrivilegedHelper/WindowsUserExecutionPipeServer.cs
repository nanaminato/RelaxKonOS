using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.UserExecution;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Dedicated authenticated pipe for ordinary Windows user file operations. It shares only the
/// Helper's caller ACL and machine secret with the privileged pipe; requests are parsed solely as
/// <see cref="UserExecutionRequest"/> and can never select a privileged operation.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsUserExecutionPipeServer(WindowsHelperPipeConfiguration configuration,
    Action<Exception> reportFailure) : IAsyncDisposable
{
    private const int MaximumRecentOperationIds = 10_000;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _recentOperationIds = new();
    private Task? _listener;

    public void Start()
    {
        if (_listener is not null) throw new InvalidOperationException("The user-execution pipe server has already started.");
        var firstPipe = CreatePipe();
        _listener = Task.Run(() => ListenAsync(firstPipe));
    }

    public async Task StopAsync()
    {
        _stopping.Cancel();
        if (_listener is null) return;
        try { await _listener.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stopping.Dispose();
    }

    private async Task ListenAsync(NamedPipeServerStream? firstPipe)
    {
        var secret = Convert.FromBase64String(configuration.SharedSecret);
        do
        {
            try
            {
                await using var pipe = firstPipe ?? CreatePipe();
                firstPipe = null;
                await pipe.WaitForConnectionAsync(_stopping.Token);
                await HandleAsync(pipe, secret, _stopping.Token);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (Exception exception) { reportFailure(exception); }
        } while (!_stopping.IsCancellationRequested);
    }

    private NamedPipeServerStream CreatePipe()
        => NamedPipeServerStreamAcl.Create(UserExecutionProtocol.WindowsPipeName(configuration.PipeName),
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            0, 0, WindowsPrivilegedPipeSecurity.Build(configuration.ServerServiceSid, configuration.DeveloperUserSids));

    private async Task HandleAsync(Stream pipe, byte[] secret, CancellationToken cancellationToken)
    {
        var frame = await ReadFrameAsync(pipe, cancellationToken);
        var envelope = JsonSerializer.Deserialize<PipeEnvelope>(frame);
        if (envelope is null || !TryDecodeAndVerify(secret, envelope, out var requestJson)) return;

        UserExecutionRequest? request;
        try { request = JsonSerializer.Deserialize<UserExecutionRequest>(requestJson, RelaxKonOSJsonOptions.Default); }
        catch (JsonException)
        {
            await WriteResultAsync(pipe, secret, Fail(UserExecutionProblemCode.InvalidRequest,
                "invalid user-execution request"), cancellationToken);
            return;
        }
        if (request is null || request.Version != UserExecutionProtocol.Version)
        {
            await WriteResultAsync(pipe, secret, Fail(UserExecutionProblemCode.InvalidProtocol,
                "unsupported user-execution protocol version"), cancellationToken);
            return;
        }
        if (!UserExecutionRequestPolicy.IsValid(request, terminal: false))
        {
            await WriteResultAsync(pipe, secret, Fail(UserExecutionProblemCode.InvalidRequest,
                "invalid user-execution request"), cancellationToken);
            return;
        }
        // Correlation metadata is required, exactly as on the elevated pipe: a request that cannot
        // be correlated must not start work whose audit trail cannot be joined up.
        if (request.Correlation is not { } correlation || !correlation.IsValid())
        {
            await WriteResultAsync(pipe, secret, Fail(UserExecutionProblemCode.InvalidRequest,
                "valid correlation metadata is required"), cancellationToken);
            return;
        }

        var operationId = request.OperationId!.Value;
        PruneRecentOperationIds();
        if (_recentOperationIds.Count >= MaximumRecentOperationIds)
        {
            await WriteResultAsync(pipe, secret, Fail(UserExecutionProblemCode.HelperUnavailable,
                "user-execution replay cache is full"), cancellationToken);
            return;
        }
        if (!_recentOperationIds.TryAdd(operationId, DateTimeOffset.UtcNow.AddMinutes(10)))
        {
            await WriteResultAsync(pipe, secret, Fail(UserExecutionProblemCode.Conflict,
                "operation id was already processed"), cancellationToken);
            return;
        }

        using var executionDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        executionDeadline.CancelAfter(TimeSpan.FromSeconds(configuration.UserExecutionTimeoutSeconds));
        var result = await WindowsUserExecutionExecutor.ExecuteAsync(request, executionDeadline.Token);
        if (!cancellationToken.IsCancellationRequested && executionDeadline.IsCancellationRequested
            && result.ProblemCode == UserExecutionProblemCode.Cancelled)
            result = Fail(UserExecutionProblemCode.TimedOut, "user-execution operation timed out");
        await WriteResultAsync(pipe, secret, result, cancellationToken);
    }

    private void PruneRecentOperationIds()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _recentOperationIds.Where(pair => pair.Value <= now))
            _recentOperationIds.TryRemove(pair.Key, out _);
    }

    private static async Task WriteResultAsync(Stream pipe, byte[] secret, UserExecutionResult result,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(result, RelaxKonOSJsonOptions.Default);
        var envelope = JsonSerializer.SerializeToUtf8Bytes(new PipeEnvelope(Convert.ToBase64String(payload),
            Sign(secret, payload)));
        await WriteFrameAsync(pipe, envelope, cancellationToken);
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length is < 1 or > UserExecutionProtocol.MaximumAuthenticatedPipeFrameBytes)
            throw new InvalidDataException("invalid user-execution pipe response size");
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
            throw new InvalidDataException("invalid user-execution pipe request size");
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
            return payload.Length <= UserExecutionProtocol.MaximumRequestBytes
                && CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(envelope.SignatureBase64),
                    HMACSHA256.HashData(secret, payload));
        }
        catch (FormatException) { return false; }
    }

    private static UserExecutionResult Fail(UserExecutionProblemCode code, string error)
        => new(false, Error: error, ProblemCode: code);
    private sealed record PipeEnvelope(string PayloadBase64, string SignatureBase64);
}
