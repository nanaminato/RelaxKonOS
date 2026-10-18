using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes;

/// <summary>An operation-scoped local callback. No grant or channel key is persisted.</summary>
public sealed class RuntimeAuthorizationServer : IAsyncDisposable
{
    private readonly CancellationTokenSource stopped = new();
    private readonly Task serving;
    public RuntimeAuthorizationChannel Channel { get; }

    public RuntimeAuthorizationServer(Guid operationId, Func<CancellationToken, Task<SingBoxCurrentAuthorization>> read)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation identity is required.", nameof(operationId));
        Channel = new(Guid.NewGuid(), operationId, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        // Create the first listener before the descriptor can be handed to Helper.
        var first = CreatePipe();
        serving = ServeAsync(first, read);
    }

    private NamedPipeServerStream CreatePipe() => new(RuntimeAuthorizationWire.Name(Channel.ChannelId),
        PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private async Task ServeAsync(NamedPipeServerStream first, Func<CancellationToken, Task<SingBoxCurrentAuthorization>> read)
    {
        NamedPipeServerStream? next = first;
        var acceptedNonces = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            while (!stopped.IsCancellationRequested)
            {
                await using (var pipe = next ?? throw new InvalidOperationException("Missing authorization listener."))
                {
                    next = null;
                    await pipe.WaitForConnectionAsync(stopped.Token);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    try
                    {
                        var request = await RuntimeAuthorizationWire.ReadAsync<RuntimeAuthorizationWire.Challenge>(pipe, 1024, deadline.Token);
                        if (request.OperationId != Channel.OperationId || !RuntimeAuthorizationWire.Hex(request.Nonce)
                            || !RuntimeAuthorizationWire.Verify(Channel, "client", request.Nonce, [], request.Proof)
                            || !acceptedNonces.Add(request.Nonce) || acceptedNonces.Count > 1024)
                            throw new RuntimePolicyException("runtime.authorization_unavailable");
                        RuntimeAuthorizationWire.Content content;
                        try { content = new(await read(deadline.Token), null); }
                        catch (RuntimePolicyException ex) { content = new(null, RuntimeAuthorizationWire.PublicProblem(ex.ProblemCode)); }
                        catch (Exception) when (!stopped.IsCancellationRequested) { content = new(null, "runtime.authorization_unavailable"); }
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(content);
                        var response = new RuntimeAuthorizationWire.Response(Convert.ToBase64String(bytes),
                            RuntimeAuthorizationWire.Proof(Channel, "server", request.Nonce, bytes));
                        await RuntimeAuthorizationWire.WriteAsync(pipe, response, RuntimeAuthorizationWire.MaximumFrameBytes, deadline.Token);
                    }
                    catch (Exception ex) when (ex is IOException or JsonException or RuntimePolicyException or OperationCanceledException)
                    { /* An unauthenticated, incomplete or timed-out connection cannot disclose material. */ }
                }
                if (!stopped.IsCancellationRequested) next = CreatePipe();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        { /* Reader loss fails closed at Helper. */ }
        finally { if (next is not null) await next.DisposeAsync(); }
    }

    public async ValueTask DisposeAsync()
    {
        await stopped.CancelAsync();
        try { await serving; }
        finally { stopped.Dispose(); }
    }
}

public static class RuntimeAuthorizationClient
{
    public static async Task<SingBoxCurrentAuthorization> ReadAsync(RuntimeAuthorizationChannel channel,
        Guid operationId, CancellationToken ct)
    {
        if (channel.ChannelId == Guid.Empty || operationId == Guid.Empty || channel.OperationId != operationId
            || !RuntimeAuthorizationWire.Hex(channel.Secret)) throw new RuntimePolicyException("runtime.authorization_unavailable");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", RuntimeAuthorizationWire.Name(channel.ChannelId), PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(deadline.Token);
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await RuntimeAuthorizationWire.WriteAsync(pipe, new RuntimeAuthorizationWire.Challenge(operationId, nonce,
                RuntimeAuthorizationWire.Proof(channel, "client", nonce, [])), 1024, deadline.Token);
            var response = await RuntimeAuthorizationWire.ReadAsync<RuntimeAuthorizationWire.Response>(pipe, RuntimeAuthorizationWire.MaximumFrameBytes, deadline.Token);
            if (response.Content is null) throw new RuntimePolicyException("runtime.authorization_unavailable");
            var bytes = Convert.FromBase64String(response.Content);
            if (!RuntimeAuthorizationWire.Verify(channel, "server", nonce, bytes, response.Proof))
                throw new RuntimePolicyException("runtime.authorization_unavailable");
            var content = JsonSerializer.Deserialize<RuntimeAuthorizationWire.Content>(bytes)
                ?? throw new RuntimePolicyException("runtime.authorization_unavailable");
            if (content.ProblemCode is not null) throw new RuntimePolicyException(RuntimeAuthorizationWire.PublicProblem(content.ProblemCode));
            if (content.Grant is not { } grant || grant.ValidUntil <= DateTimeOffset.UtcNow || grant.ValidUntil > DateTimeOffset.UtcNow.AddMinutes(5))
                throw new RuntimePolicyException("runtime.authorization_unavailable");
            return grant;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        { throw new RuntimePolicyException("runtime.authorization_unavailable"); }
    }
}

internal static class RuntimeAuthorizationWire
{
    internal const int MaximumFrameBytes = 8 * 1024 * 1024;
    internal static string Name(Guid id) => "RelaxKonOS.RuntimeAuthorization." + id.ToString("N");
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record Challenge(Guid OperationId, string Nonce, string Proof);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record Response(string Content, string Proof);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record Content(SingBoxCurrentAuthorization? Grant, string? ProblemCode);
    internal static bool Hex(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    internal static string Proof(RuntimeAuthorizationChannel channel, string role, string nonce, byte[] content)
    {
        var context = Encoding.UTF8.GetBytes($"RelaxKonOS.RuntimeAuthorization.v1\n{role}\n{channel.ChannelId:N}\n{channel.OperationId:N}\n{nonce}\n");
        var key = Convert.FromHexString(channel.Secret);
        try
        {
            using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
            hash.AppendData(context); hash.AppendData(content);
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    internal static bool Verify(RuntimeAuthorizationChannel channel, string role, string nonce, byte[] content, string? proof) =>
        Hex(proof) && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Proof(channel, role, nonce, content)), Convert.FromHexString(proof!));
    internal static string PublicProblem(string code) => code is "runtime.credentials_revoked" or "runtime.certificate_unavailable"
        or "runtime.certificate_changed" or "runtime.publication_unconfirmed" ? code : "runtime.authorization_unavailable";
    internal static async Task WriteAsync<T>(Stream stream, T value, int limit, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > limit) throw new RuntimePolicyException("runtime.authorization_unavailable");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct); await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct);
    }
    internal static async Task<T> ReadAsync<T>(Stream stream, int limit, CancellationToken ct)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 1 || length > limit) throw new RuntimePolicyException("runtime.authorization_unavailable");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, ct);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new RuntimePolicyException("runtime.authorization_unavailable");
    }
}
