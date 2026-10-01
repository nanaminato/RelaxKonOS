using RelaxKonOS.Protocol.UserExecution;

namespace RelaxKonOS.Server.UserExecution;

/// <summary>Seekable HTTP file body backed by bounded, identity-checked reads.</summary>
public sealed class UserExecutionReadStream(long length,
    Func<long, int, CancellationToken, Task<UserExecutionFileRead>> read) : Stream
{
    private long position;
    private bool disposed;
    private byte[] buffered = [];
    private long bufferedOffset;
    public override bool CanRead => !disposed;
    public override bool CanSeek => !disposed;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (buffer.Length == 0 || position >= length) return 0;
        cancellationToken.ThrowIfCancellationRequested();
        if (buffered.Length == 0 || position < bufferedOffset || position >= bufferedOffset + buffered.Length)
        {
            var count = (int)Math.Min(UserExecutionFileReads.MaximumChunkBytes, length - position);
            var chunk = await read(position, count, cancellationToken);
            var bytes = Convert.FromBase64String(chunk.ContentBase64);
            if (chunk.Length != length || bytes.Length != count)
                throw new IOException("File changed during download.");
            buffered = bytes;
            bufferedOffset = position;
        }
        var start = (int)(position - bufferedOffset);
        var copied = Math.Min(buffer.Length, buffered.Length - start);
        buffered.AsMemory(start, copied).CopyTo(buffer);
        position += copied;
        return copied;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(position + offset),
            SeekOrigin.End => checked(length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0) throw new IOException("Invalid file position.");
        return position = target;
    }
    protected override void Dispose(bool disposing) { disposed = true; buffered = []; base.Dispose(disposing); }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
