namespace RelaxKonOS.Protocol.UserExecution;

/// <summary>A bounded read of an already authorised file, including its current total length.</summary>
public sealed record UserExecutionFileRead(string ContentBase64, string FileName, string ContentType, long Length);

public static class UserExecutionFileReads
{
    public const int MaximumChunkBytes = 1024 * 1024;

    // The caller must open the stream under the effective user's identity before calling this method.
    public static UserExecutionFileRead Read(Stream stream, string fileName, string contentType,
        long offset, long count, CancellationToken cancellationToken = default)
    {
        if (offset < 0 || count < 0 || count > MaximumChunkBytes)
            throw new ArgumentException("Invalid file read range.");
        var length = stream.Length;
        if (offset > length) throw new IOException("File changed during download.");
        stream.Seek(offset, SeekOrigin.Begin);
        var bytes = new byte[(int)Math.Min(count, length - offset)];
        var read = 0;
        while (read < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var received = stream.Read(bytes, read, bytes.Length - read);
            if (received == 0) throw new EndOfStreamException("File changed during download.");
            read += received;
        }
        return new(Convert.ToBase64String(bytes), fileName, contentType, length);
    }
}
