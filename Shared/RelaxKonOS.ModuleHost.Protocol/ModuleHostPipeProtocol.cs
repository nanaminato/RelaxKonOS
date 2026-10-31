using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelaxKonOS.ModuleHost.Protocol;

public sealed record ModuleHostLaunchRequest(Guid LaunchId, string ModuleId, string Version,
    string ManifestSha256, string ServiceToken, string? BrokerUrl);
public sealed record ModuleHostPipeFrame(long Sequence, string Type, string Payload);

/// <summary>Bounded, authenticated local launch channel. The machine secret never enters the child environment.</summary>
public static class ModuleHostPipeProtocol
{
    public const int MaximumFrameBytes = 256 * 1024;
    public const string ClientDirection = "server-to-helper";
    public const string HelperDirection = "helper-to-server";
    public static string PipeName(string privilegedPipe) => privilegedPipe + ".module-host";
    private sealed record Envelope(string Payload, string Signature);
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8,
    };

    public static async Task WriteAsync(Stream stream, ModuleHostPipeFrame frame, byte[] secret,
        string nonce, string direction, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, Json);
        var envelope = JsonSerializer.SerializeToUtf8Bytes(new Envelope(Convert.ToBase64String(payload),
            Convert.ToBase64String(Sign(payload, secret, nonce, direction))), Json);
        if (envelope.Length > MaximumFrameBytes) throw new InvalidDataException("module-host-frame-size");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, envelope.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(envelope, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<ModuleHostPipeFrame> ReadAsync(Stream stream, byte[] secret, string nonce,
        string direction, long sequence, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaximumFrameBytes) throw new InvalidDataException("module-host-frame-size");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        var envelope = Read<Envelope>(bytes);
        var payload = Convert.FromBase64String(envelope.Payload);
        var signature = Convert.FromBase64String(envelope.Signature);
        if (!CryptographicOperations.FixedTimeEquals(Sign(payload, secret, nonce, direction), signature))
            throw new InvalidDataException("module-host-frame-authentication");
        var frame = Read<ModuleHostPipeFrame>(payload);
        if (frame.Sequence != sequence || frame.Type is not ("hello" or "launch" or "command" or "stdout" or "stderr" or "exit" or "faulted"))
            throw new InvalidDataException("module-host-frame-order");
        return frame;
    }

    public static T Read<T>(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        Unique(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("module-host-frame-invalid");
    }
    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("module-host-frame-invalid");
                Unique(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) Unique(item);
    }
    private static byte[] Sign(byte[] payload, byte[] secret, string nonce, string direction)
    {
        if (secret.Length < 32) throw new InvalidDataException("module-host-key-invalid");
        var domain = Encoding.UTF8.GetBytes("RelaxKonOS.ModuleHostPipe.v1\n" + nonce + "\n" + direction + "\n");
        var input = new byte[domain.Length + payload.Length];
        domain.CopyTo(input, 0);
        payload.CopyTo(input, domain.Length);
        return HMACSHA256.HashData(secret, input);
    }
}
