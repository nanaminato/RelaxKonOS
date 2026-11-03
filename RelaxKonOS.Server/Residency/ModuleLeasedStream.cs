namespace RelaxKonOS.Server.Residency;

/// <summary>Transfers module ownership to a stream until cleanup and all outstanding operations finish.</summary>
internal sealed class ModuleLeasedStream(Stream inner, ModuleLease lease) : Stream
{
    private readonly object _gate = new();
    private Stream? _inner = inner;
    private Stream Inner => _inner ?? throw new ObjectDisposedException(nameof(ModuleLeasedStream));
    private bool _cleanupSucceeded;
    private ModuleLease? _lease = lease;
    private int _operations;
    private bool _closing;
    private bool _cleaned;
    private Task? _cleanup;

    private Operation Enter()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            _operations++;
            return new Operation(this);
        }
    }
    private void Exit()
    {
        ModuleLease? release = null;
        lock (_gate)
        {
            _operations--;
            if (_cleaned && _operations == 0)
            {
                if (_cleanupSucceeded) _inner = null;
                release = _lease; _lease = null;
            }
        }
        release?.Dispose();
    }
    private sealed class Operation(ModuleLeasedStream owner) : IDisposable
    { public void Dispose() => owner.Exit(); }
    private Task Cleanup(bool asynchronous)
    {
        lock (_gate)
        {
            _closing = true;
            return _cleanup ??= CleanupCoreAsync(asynchronous);
        }
    }
    private async Task CleanupCoreAsync(bool asynchronous)
    {
        try
        {
            if (asynchronous) await Inner.DisposeAsync().ConfigureAwait(false);
            else Inner.Dispose();
            lock (_gate) _cleanupSucceeded = true;
        }
        catch
        {
            _lease?.Quarantine(this);
            throw;
        }
        finally
        {
            ModuleLease? release = null;
            lock (_gate)
            {
                _cleaned = true;
                if (_operations == 0)
                {
                    // Keep failed cleanup resources for quarantine; successful wrappers must not
                    // root a module stream/buffer after callers retain the disposed wrapper.
                    if (_cleanupSucceeded) _inner = null;
                    release = _lease; _lease = null;
                }
            }
            release?.Dispose();
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) Cleanup(false).GetAwaiter().GetResult();
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        await Cleanup(true).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
    public override bool CanRead { get { lock (_gate) return !_closing && Inner.CanRead; } }
    public override bool CanWrite { get { lock (_gate) return !_closing && Inner.CanWrite; } }
    public override bool CanSeek { get { lock (_gate) return !_closing && Inner.CanSeek; } }
    public override bool CanTimeout { get { lock (_gate) return !_closing && Inner.CanTimeout; } }
    public override long Length { get { using var operation = Enter(); return Inner.Length; } }
    public override long Position
    { get { using var operation = Enter(); return Inner.Position; } set { using var operation = Enter(); Inner.Position = value; } }
    public override int ReadTimeout
    { get { using var operation = Enter(); return Inner.ReadTimeout; } set { using var operation = Enter(); Inner.ReadTimeout = value; } }
    public override int WriteTimeout
    { get { using var operation = Enter(); return Inner.WriteTimeout; } set { using var operation = Enter(); Inner.WriteTimeout = value; } }
    public override void Flush() { using var operation = Enter(); Inner.Flush(); }
    public override int Read(byte[] buffer, int offset, int count) { using var operation = Enter(); return Inner.Read(buffer, offset, count); }
    public override int Read(Span<byte> buffer) { using var operation = Enter(); return Inner.Read(buffer); }
    public override int ReadByte() { using var operation = Enter(); return Inner.ReadByte(); }
    public override void Write(byte[] buffer, int offset, int count) { using var operation = Enter(); Inner.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { using var operation = Enter(); Inner.Write(buffer); }
    public override void WriteByte(byte value) { using var operation = Enter(); Inner.WriteByte(value); }
    public override long Seek(long offset, SeekOrigin origin) { using var operation = Enter(); return Inner.Seek(offset, origin); }
    public override void SetLength(long value) { using var operation = Enter(); Inner.SetLength(value); }
    public override async Task FlushAsync(CancellationToken cancellationToken)
    { using var operation = Enter(); await Inner.FlushAsync(cancellationToken).ConfigureAwait(false); }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { using var operation = Enter(); return await Inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { using var operation = Enter(); return await Inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { using var operation = Enter(); await Inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { using var operation = Enter(); await Inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); }
}
