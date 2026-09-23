using System.Buffers.Binary;

namespace RelaxKonOS.Protocol.UserExecution;

/// <summary>
/// Framing used after the initial JSON <see cref="UserExecutionRequest"/> on the dedicated
/// Helper terminal stdin stream. Keeping control frames separate from terminal bytes prevents a
/// resize from being interpreted as shell input.
/// </summary>
public static class UserTerminalStreamProtocol
{
    private const byte InputKind = 1;
    private const byte ResizeKind = 2;
    private const byte CloseKind = 3;

    public static void WriteInput(Stream stream, ReadOnlySpan<byte> input)
    {
        if (input.Length is <= 0 or > UserExecutionProtocol.MaximumTerminalInputBytes)
            throw new ArgumentOutOfRangeException(nameof(input));
        Span<byte> header = stackalloc byte[5];
        header[0] = InputKind;
        BinaryPrimitives.WriteInt32BigEndian(header[1..], input.Length);
        stream.Write(header);
        stream.Write(input);
        stream.Flush();
    }

    public static void WriteResize(Stream stream, int columns, int rows, int widthPixels, int heightPixels)
    {
        ValidateDimensions(columns, rows, widthPixels, heightPixels);
        Span<byte> frame = stackalloc byte[17];
        frame[0] = ResizeKind;
        BinaryPrimitives.WriteInt32BigEndian(frame[1..5], columns);
        BinaryPrimitives.WriteInt32BigEndian(frame[5..9], rows);
        BinaryPrimitives.WriteInt32BigEndian(frame[9..13], widthPixels);
        BinaryPrimitives.WriteInt32BigEndian(frame[13..17], heightPixels);
        stream.Write(frame);
        stream.Flush();
    }

    public static void WriteClose(Stream stream)
    {
        stream.WriteByte(CloseKind);
        stream.Flush();
    }

    public static async ValueTask<UserTerminalFrame?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var kind = new byte[1];
        if (await stream.ReadAsync(kind, cancellationToken) == 0)
            return null;
        switch (kind[0])
        {
            case InputKind:
            {
                var lengthBytes = new byte[4];
                await stream.ReadExactlyAsync(lengthBytes, cancellationToken);
                var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
                if (length is <= 0 or > UserExecutionProtocol.MaximumTerminalInputBytes)
                    throw new InvalidDataException("Invalid terminal input frame length.");
                var payload = new byte[length];
                await stream.ReadExactlyAsync(payload, cancellationToken);
                return new(UserTerminalFrameKind.Input, payload);
            }
            case ResizeKind:
            {
                var payload = new byte[16];
                await stream.ReadExactlyAsync(payload, cancellationToken);
                var columns = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(0, 4));
                var rows = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(4, 4));
                var widthPixels = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(8, 4));
                var heightPixels = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(12, 4));
                try { ValidateDimensions(columns, rows, widthPixels, heightPixels); }
                catch (ArgumentOutOfRangeException exception)
                { throw new InvalidDataException("Invalid terminal resize dimensions.", exception); }
                return new(UserTerminalFrameKind.Resize, Columns: columns, Rows: rows,
                    WidthPixels: widthPixels, HeightPixels: heightPixels);
            }
            case CloseKind:
                return new(UserTerminalFrameKind.Close);
            default:
                throw new InvalidDataException("Unknown terminal control frame.");
        }
    }

    public static void ValidateDimensions(int columns, int rows, int widthPixels, int heightPixels)
    {
        if (columns is < 1 or > 1000 || rows is < 1 or > 1000
            || widthPixels is < 0 or > 100_000 || heightPixels is < 0 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(columns), "Invalid terminal dimensions.");
    }
}

public enum UserTerminalFrameKind
{
    Input,
    Resize,
    Close,
}

public sealed record UserTerminalFrame(
    UserTerminalFrameKind Kind,
    byte[]? Input = null,
    int Columns = 0,
    int Rows = 0,
    int WidthPixels = 0,
    int HeightPixels = 0);
