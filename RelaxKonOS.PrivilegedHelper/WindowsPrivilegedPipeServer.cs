using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Runtime.Versioning;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Authenticated local-pipe host shared by the LocalSystem service and the developer console
/// host. It deliberately knows nothing about service lifecycle or process identity.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPrivilegedPipeServer(WindowsHelperPipeConfiguration configuration, Action<Exception> reportFailure)
    : IAsyncDisposable
{
    private const int MaximumRecentOperationIds = 10_000;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _recentOperationIds = new();
    private Task? _listener;

    public void Start() => _listener = Task.Run(ListenAsync);

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

    private async Task ListenAsync()
    {
        var secret = Convert.FromBase64String(configuration.SharedSecret);
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(_stopping.Token);
                await HandleAsync(pipe, secret, _stopping.Token);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (Exception exception) { reportFailure(exception); }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        if (!string.IsNullOrWhiteSpace(configuration.ServerServiceSid))
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(configuration.ServerServiceSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        if (!string.IsNullOrWhiteSpace(configuration.DeveloperUserSid))
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(configuration.DeveloperUserSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(configuration.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough, PrivilegedOperationProtocol.MaximumRequestBytes,
            PrivilegedOperationProtocol.MaximumRequestBytes, security);
    }

    private async Task HandleAsync(Stream pipe, byte[] secret, CancellationToken cancellationToken)
    {
        var frame = await ReadFrameAsync(pipe, cancellationToken);
        var envelope = JsonSerializer.Deserialize<PipeEnvelope>(frame);
        if (envelope is null || !TryDecodeAndVerify(secret, envelope, out var requestJson)) return;
        var request = JsonSerializer.Deserialize<PrivilegedOperationRequest>(requestJson);
        if (request is null || request.Version != PrivilegedOperationProtocol.Version)
        {
            await WriteResultAsync(pipe, secret, new(false, 64, Error: "unsupported protocol version", ProblemCode: PrivilegedProblemCode.InvalidProtocol), cancellationToken);
            return;
        }
        if (request.OperationId is not { } operationId || operationId == Guid.Empty)
        {
            await WriteResultAsync(pipe, secret, new(false, 64, Error: "operation id is required", ProblemCode: PrivilegedProblemCode.InvalidRequest), cancellationToken);
            return;
        }
        PruneRecentOperationIds();
        if (_recentOperationIds.Count >= MaximumRecentOperationIds)
        {
            await WriteResultAsync(pipe, secret, new(false, 69, Error: "operation replay cache is full", ProblemCode: PrivilegedProblemCode.HelperUnavailable), cancellationToken);
            return;
        }
        if (!_recentOperationIds.TryAdd(operationId, DateTimeOffset.UtcNow.AddMinutes(10)))
        {
            await WriteResultAsync(pipe, secret, new(false, 17, Error: "operation id was already processed", ProblemCode: PrivilegedProblemCode.Conflict), cancellationToken);
            return;
        }

        // The executable was integrity-checked before the production service starts. Keeping
        // the executor in-process also avoids a second, debugger-hostile worker process.
        var result = await PrivilegedOperationExecutor.ExecuteAsync(request,
            new PrivilegedOperationPolicy(configuration.FileAllowedRoots, configuration.AllowedServiceIds));
        await WriteResultAsync(pipe, secret, result, cancellationToken);
    }

    private void PruneRecentOperationIds()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _recentOperationIds.Where(pair => pair.Value <= now))
            _recentOperationIds.TryRemove(pair.Key, out _);
    }

    private static async Task WriteResultAsync(Stream pipe, byte[] secret, PrivilegedOperationResult result, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(result);
        await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new PipeEnvelope(Convert.ToBase64String(payload), Sign(secret, payload))), cancellationToken);
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
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
        if (length is < 1 or > PrivilegedOperationProtocol.MaximumRequestBytes) throw new InvalidDataException("invalid pipe request size");
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
            return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(envelope.SignatureBase64), HMACSHA256.HashData(secret, payload));
        }
        catch (FormatException) { return false; }
    }

    private sealed record PipeEnvelope(string PayloadBase64, string SignatureBase64);
}

internal sealed record WindowsHelperPipeConfiguration(string PipeName, string SharedSecret,
    IReadOnlyList<string> FileAllowedRoots, IReadOnlyList<string> AllowedServiceIds,
    string? ServerServiceSid = null, string? DeveloperUserSid = null);
