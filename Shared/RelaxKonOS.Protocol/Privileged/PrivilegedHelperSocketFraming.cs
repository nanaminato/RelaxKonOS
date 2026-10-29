using System.Buffers.Binary;

namespace RelaxKonOS.Protocol.Privileged;

/// <summary>
/// Explicit framing for the Unix socket transport: a 4-byte little-endian length followed by a
/// bounded UTF-8 JSON payload. One read is never assumed to equal one message, and the length is
/// validated before any payload buffer is allocated.
/// </summary>
public static class PrivilegedHelperSocketFraming
{
    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length < 1 || payload.Length > PrivilegedHelperTransportProtocol.MaximumFrameBytes)
            throw new InvalidDataException("The helper frame exceeds the protocol limit.");
        var frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsMemory(sizeof(int)));
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Returns <see langword="null"/> only for a clean end of stream before a frame starts. A
    /// truncated frame, an invalid length or a payload over <paramref name="maximumBytes"/> throws,
    /// because the connection can no longer be trusted to carry a complete message.
    /// </summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes < 1 || maximumBytes > PrivilegedHelperTransportProtocol.MaximumFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var header = new byte[sizeof(int)];
        if (!await ReadHeaderAsync(stream, header, cancellationToken)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 1 || length > maximumBytes)
            throw new InvalidDataException("The helper frame length is invalid.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    private static async Task<bool> ReadHeaderAsync(Stream stream, Memory<byte> header, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < header.Length)
        {
            var count = await stream.ReadAsync(header[read..], cancellationToken);
            if (count == 0) return read == 0 ? false : throw new EndOfStreamException("A helper frame header was truncated.");
            read += count;
        }
        return true;
    }
}
