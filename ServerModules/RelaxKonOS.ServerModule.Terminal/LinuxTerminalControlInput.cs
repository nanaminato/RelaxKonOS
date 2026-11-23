using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RelaxKonOS.Server.Terminal;

/// <summary>Exclusive worker stdin. Cancellation drains the actual native read, including partial frames.</summary>
internal sealed class LinuxTerminalControlInput : Stream
{
    private readonly int _originalFlags;
    private int _activeReads;
    private bool _disposed;
    internal int ActiveReads => Volatile.Read(ref _activeReads);
    internal LinuxTerminalControlInput()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        _originalFlags = Fcntl(0, 3, 0);
        if (_originalFlags < 0 || Fcntl(0, 4, _originalFlags | 0x800) != 0)
            throw new IOException("terminal.control_input_unavailable", new Win32Exception(Marshal.GetLastPInvokeError()));
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty) return 0;
        var bytes = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length, 65536));
        Interlocked.Increment(ref _activeReads);
        try
        {
            // Never cancel the scheduled Task or detach its wait. The native loop itself checks
            // cancellation and returns before its rented buffer and descriptor flags are released.
            var count = await Task.Run(() => ReadReady(bytes, Math.Min(buffer.Length, 65536), cancellationToken), CancellationToken.None);
            bytes.AsMemory(0, count).CopyTo(buffer);
            return count;
        }
        finally { ArrayPool<byte>.Shared.Return(bytes, clearArray: true); Interlocked.Decrement(ref _activeReads); }
    }
    private static int ReadReady(byte[] bytes, int maximum, CancellationToken cancellationToken)
    {
        var descriptor = new PollDescriptor { Descriptor = 0, Events = 1 };
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            descriptor.ReturnedEvents = 0;
            var ready = Poll(ref descriptor, 1, 50);
            if (ready == 0) continue;
            if (ready < 0 && Marshal.GetLastPInvokeError() == 4) continue;
            if (ready < 0 || (descriptor.ReturnedEvents & (1 | 16)) == 0)
                throw new IOException("terminal.control_input_failed", new Win32Exception(Marshal.GetLastPInvokeError()));
            cancellationToken.ThrowIfCancellationRequested();
            var count = Read(0, bytes, (nuint)maximum);
            if (count >= 0) return checked((int)count);
            if (Marshal.GetLastPInvokeError() is 4 or 11) continue;
            throw new IOException("terminal.control_input_failed", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (ActiveReads != 0) throw new InvalidOperationException("Terminal stdin still has an owned native read.");
        if (Fcntl(0, 4, _originalFlags) != 0) throw new IOException("terminal.control_input_restore_failed", new Win32Exception(Marshal.GetLastPInvokeError()));
        _disposed = true;
        base.Dispose(disposing);
    }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    [StructLayout(LayoutKind.Sequential)] private struct PollDescriptor { public int Descriptor; public short Events; public short ReturnedEvents; }
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] private static extern int Fcntl(int fd, int command, int argument);
    [DllImport("libc", EntryPoint = "poll", SetLastError = true)] private static extern int Poll(ref PollDescriptor descriptor, nuint count, int timeout);
    [DllImport("libc", EntryPoint = "read", SetLastError = true)] private static extern nint Read(int descriptor, [Out] byte[] bytes, nuint count);
}
