namespace RelaxKonOS.Server.Residency;

/// <summary>Transfers module ownership to a stream until cleanup and all outstanding operations finish.</summary>
internal sealed class ModuleLeasedStream(Stream inner, ModuleLease lease) : Stream
{
    private readonly object _gate = new();
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
            if (_cleaned && _operations == 0) { release = _lease; _lease = null; }
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
            if (asynchronous) await inner.DisposeAsync().ConfigureAwait(false);
            else inner.Dispose();
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
                if (_operations == 0) { release = _lease; _lease = null; }
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
    public override bool CanRead { get { lock (_gate) return !_closing && inner.CanRead; } }
    public override bool CanWrite { get { lock (_gate) return !_closing && inner.CanWrite; } }
    public override bool CanSeek { get { lock (_gate) return !_closing && inner.CanSeek; } }
    public override bool CanTimeout { get { lock (_gate) return !_closing && inner.CanTimeout; } }
    public override long Length { get { using var operation = Enter(); return inner.Length; } }
    public override long Position
    { get { using var operation = Enter(); return inner.Position; } set { using var operation = Enter(); inner.Position = value; } }
    public override int ReadTimeout
    { get { using var operation = Enter(); return inner.ReadTimeout; } set { using var operation = Enter(); inner.ReadTimeout = value; } }
    public override int WriteTimeout
    { get { using var operation = Enter(); return inner.WriteTimeout; } set { using var operation = Enter(); inner.WriteTimeout = value; } }
    public override void Flush() { using var operation = Enter(); inner.Flush(); }
    public override int Read(byte[] buffer, int offset, int count) { using var operation = Enter(); return inner.Read(buffer, offset, count); }
    public override int Read(Span<byte> buffer) { using var operation = Enter(); return inner.Read(buffer); }
    public override int ReadByte() { using var operation = Enter(); return inner.ReadByte(); }
    public override void Write(byte[] buffer, int offset, int count) { using var operation = Enter(); inner.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { using var operation = Enter(); inner.Write(buffer); }
    public override void WriteByte(byte value) { using var operation = Enter(); inner.WriteByte(value); }
    public override long Seek(long offset, SeekOrigin origin) { using var operation = Enter(); return inner.Seek(offset, origin); }
    public override void SetLength(long value) { using var operation = Enter(); inner.SetLength(value); }
    public override async Task FlushAsync(CancellationToken cancellationToken)
    { using var operation = Enter(); await inner.FlushAsync(cancellationToken).ConfigureAwait(false); }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { using var operation = Enter(); return await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { using var operation = Enter(); return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { using var operation = Enter(); await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { using var operation = Enter(); await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); }
}
