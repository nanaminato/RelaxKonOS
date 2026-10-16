using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer;



/// <summary>Windows Shell virtual files. Publishing only enumerates metadata; content is opened by paste.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsFileClipboard
{
    internal static Action<string>? Trace;
    private static VirtualFiles? _current;
    private static IntPtr _owner;
    private static readonly Lazy<ClipboardApartment> Apartment = new(() => new());
    // Delayed rendering can change the sequence number; the dedicated OLE owner
    // remains stable until another application replaces the clipboard.
    public static bool IsCurrent => _current is not null && _owner != IntPtr.Zero && _owner == GetClipboardOwner();

    // Shell callbacks can block on network IO. Keep them off Avalonia's UI apartment
    // so progress, cancellation and other application windows remain responsive.
    private sealed class ClipboardApartment
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _work = new();
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ClipboardApartment()
        {
            var thread = new Thread(() =>
            {
                var initialization = OleInitialize(IntPtr.Zero);
                if (initialization < 0) { _ready.SetException(Marshal.GetExceptionForHR(initialization)!); return; }
                _ready.SetResult();
                foreach (var action in Pump())
                {
                    action();
                }
                IEnumerable<Action> Pump()
                {
                    while (true)
                    {
                        if (_work.TryTake(out var action, 10)) yield return action;
                        while (PeekMessage(out var message, IntPtr.Zero, 0, 0, 1))
                        { TranslateMessage(ref message); DispatchMessage(ref message); }
                    }
                }
            }) { IsBackground = true, Name = "RelaxKonOS file clipboard" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        public async Task InvokeAsync(Action action)
        {
            await _ready.Task.ConfigureAwait(false);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _work.Add(() => { try { action(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } });
            await completion.Task.ConfigureAwait(false);
        }
    }

    public static async Task PublishAsync(IExplorerClient client, IReadOnlyList<FileSystemEntryDto> entries,
        Func<bool> isSessionCurrent, Func<string, long, ClipboardFileTransfer>? beginTransfer = null)
    {
        var files = new List<VirtualFile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        async Task Add(string path, string name, bool directory, long size, int depth)
        {
            if (!isSessionCurrent()) throw new IOException("The source session has changed.");
            if (depth > 128 || files.Count >= 100000) throw new IOException("The clipboard selection is too large.");
            ValidateName(name);
            if (!names.Add(name)) throw new IOException("The selection contains duplicate Windows file names.");
            files.Add(new(path, name, directory, size));
            if (!directory) return;
            var listing = await client.GetDirectoryAsync(path);
            foreach (var child in listing.Directories)
            {
                ValidateComponent(child.Name);
                await Add(child.Path, name + "\\" + child.Name, true, 0, depth + 1);
            }
            foreach (var child in listing.Files)
            {
                ValidateComponent(child.Name);
                await Add(child.Path, name + "\\" + child.Name, false, child.Size, depth + 1);
            }
        }
        foreach (var entry in entries)
        {
            ValidateComponent(entry.Name);
            await Add(entry.Path, entry.Name, entry.Type != FileSystemEntryType.File, entry.Size ?? 0, 0);
        }
        if (!isSessionCurrent()) throw new IOException("The source session has changed.");
        await Apartment.Value.InvokeAsync(() =>
        {
            if (!isSessionCurrent()) throw new IOException("The source session has changed.");
            var data = new VirtualFiles(files, client, isSessionCurrent, beginTransfer);
            Marshal.ThrowExceptionForHR(OleSetClipboard(data));
            _current = data;
            _owner = GetClipboardOwner();
        });
    }

    internal static void ValidateComponent(string name)
    {
        if (name.Contains('\\')) throw new IOException("A source name contains a Windows path separator: " + name);
        ValidateName(name);
    }

    internal static void ValidateName(string name)
    {
        if (name.Length >= 260 || name.Split('\\').Any(part => string.IsNullOrWhiteSpace(part)
            || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
            || part.IndexOfAny(['<', '>', ':', '"', '/', '|', '?', '*']) >= 0 || part.Any(c => c < 32)
            || new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(part.Split('.')[0], StringComparer.OrdinalIgnoreCase)))
            throw new IOException("A source name cannot be represented by Windows: " + name);
    }

    [ComVisible(true), Guid("3D8B0590-F691-11D2-8EA9-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDataObjectAsyncCapability
    {
        [PreserveSig] int SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool enabled);
        [PreserveSig] int GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool enabled);
        [PreserveSig] int StartOperation(IBindCtx? context);
        [PreserveSig] int InOperation([MarshalAs(UnmanagedType.Bool)] out bool active);
        [PreserveSig] int EndOperation(int result, IBindCtx? context, uint effects);
    }

    internal sealed record VirtualFile(string Path, string Name, bool Directory, long Size);

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class VirtualFiles : IDataObject, IDataObjectAsyncCapability
    {
        private readonly IReadOnlyList<VirtualFile> _files;
        private readonly IExplorerClient _client;
        private readonly Func<bool> _valid;
        private readonly Func<string, long, ClipboardFileTransfer>? _beginTransfer;
        private bool _asyncMode = true;
        private bool _inOperation;
        private readonly List<WeakReference<ContentStream>> _streams = [];
        private readonly short _descriptor = unchecked((short)RegisterClipboardFormat("FileGroupDescriptorW"));
        private readonly short _contents = unchecked((short)RegisterClipboardFormat("FileContents"));
        private readonly short _effect = unchecked((short)RegisterClipboardFormat("Preferred DropEffect"));
        internal VirtualFiles(IReadOnlyList<VirtualFile> files, IExplorerClient client, Func<bool> valid,
            Func<string, long, ClipboardFileTransfer>? beginTransfer = null)
            => (_files, _client, _valid, _beginTransfer) = (files, client, valid, beginTransfer);
        private FORMATETC Format(short id, TYMED medium, int index = -1) => new()
            { cfFormat = id, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = medium };
        public int QueryGetData(ref FORMATETC f)
        {
            Trace?.Invoke($"Query {f.cfFormat} {f.tymed} {f.lindex} {f.dwAspect}");
            if ((f.dwAspect & DVASPECT.DVASPECT_CONTENT) == 0) return unchecked((int)0x8004006B);
            if ((f.cfFormat == _descriptor || f.cfFormat == _effect) && (f.tymed & TYMED.TYMED_HGLOBAL) != 0 && f.lindex == -1) return 0;
            if (f.cfFormat == _contents && (f.tymed & TYMED.TYMED_ISTREAM) != 0
                && (f.lindex == -1 || (f.lindex >= 0 && f.lindex < _files.Count && !_files[f.lindex].Directory))) return 0;
            return unchecked((int)0x80040064);
        }
        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            Trace?.Invoke($"Get {format.cfFormat} {format.tymed} {format.lindex}");
            Marshal.ThrowExceptionForHR(QueryGetData(ref format));
            if (!_valid()) throw new COMException("The source session has changed.", unchecked((int)0x80030005));
            if (format.cfFormat == _contents)
            {
                if (format.lindex < 0) throw new COMException("A content request requires a file index.", unchecked((int)0x80040068));
                var file = _files[format.lindex];
                var stream = new ContentStream(file.Size, token => Task.Run(async () =>
                {
                    if (!_valid()) throw new IOException("The source session has changed.");
                    var result = await _client.DownloadAsync(file.Path, token).ConfigureAwait(false);
                    if (result is null) throw new FileNotFoundException(file.Name);
                    if (!_valid()) { result.Value.Stream.Dispose(); throw new IOException("The source session has changed."); }
                    return result.Value.Stream;
                }).GetAwaiter().GetResult(), _valid, () => _beginTransfer?.Invoke(file.Path, file.Size));
                _streams.RemoveAll(reference => !reference.TryGetTarget(out _));
                _streams.Add(new(stream));
                medium = new() { tymed = TYMED.TYMED_ISTREAM, unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IStream)) };
                return;
            }
            byte[] bytes;
            if (format.cfFormat == _effect) bytes = BitConverter.GetBytes(1); // Copy; external moves never delete a remote source.
            else
            {
                using var output = new MemoryStream();
                using var writer = new BinaryWriter(output, Encoding.Unicode);
                writer.Write(_files.Count);
                foreach (var file in _files)
                {
                    writer.Write(0x80004044u); // FD_UNICODE | FD_PROGRESSUI | FD_ATTRIBUTES | FD_FILESIZE
                    writer.Write(new byte[32]); // CLSID, SIZEL, POINTL
                    writer.Write(file.Directory ? 0x10u : 0x80u);
                    writer.Write(new byte[24]); // times intentionally unspecified
                    writer.Write((uint)((ulong)file.Size >> 32));
                    writer.Write((uint)file.Size);
                    var name = Encoding.Unicode.GetBytes(file.Name);
                    writer.Write(name); writer.Write(new byte[520 - name.Length]);
                }
                bytes = output.ToArray();
            }
            var handle = GlobalAlloc(0x42, (UIntPtr)bytes.Length);
            if (handle == IntPtr.Zero) throw new OutOfMemoryException();
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) { GlobalFree(handle); throw new OutOfMemoryException(); }
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); } finally { GlobalUnlock(handle); }
            medium = new() { tymed = TYMED.TYMED_HGLOBAL, unionmember = handle };
        }
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
        {
            if (direction != DATADIR.DATADIR_GET) throw new NotSupportedException();
            // Advertise real content indices. The OLE clipboard wrapper normalizes these
            // to -1 for capability probing; GetData still supplies the actual file index.
            return new Formats(new[] { Format(_descriptor, TYMED.TYMED_HGLOBAL), Format(_effect, TYMED.TYMED_HGLOBAL) }
                .Concat(_files.Select((file, index) => (file, index)).Where(item => !item.file.Directory)
                    .Select(item => Format(_contents, TYMED.TYMED_ISTREAM, item.index))).ToArray());
        }
        public int SetAsyncMode(bool enabled) { _asyncMode = enabled; return 0; }
        public int GetAsyncMode(out bool enabled) { enabled = _asyncMode; return 0; }
        public int StartOperation(IBindCtx? context) { Trace?.Invoke("Async Start"); _inOperation = true; return 0; }
        public int InOperation(out bool active) { active = _inOperation; return 0; }
        public int EndOperation(int result, IBindCtx? context, uint effects)
        {
            Trace?.Invoke($"Async End {result:X8}");
            _inOperation = false;
            foreach (var reference in _streams)
                if (reference.TryGetTarget(out var stream)) stream.EndOperation(result);
            _streams.Clear();
            return 0;
        }
        public void GetDataHere(ref FORMATETC f, ref STGMEDIUM m) => throw new NotSupportedException();
        public int GetCanonicalFormatEtc(ref FORMATETC f, out FORMATETC result) { result = f; result.ptd = IntPtr.Zero; return 0x40130; }
        public void SetData(ref FORMATETC f, ref STGMEDIUM m, bool release) => throw new NotSupportedException();
        public int DAdvise(ref FORMATETC f, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
        public void DUnadvise(int connection) => throw new NotSupportedException();
        public int EnumDAdvise(out IEnumSTATDATA enumerator) { enumerator = null!; return unchecked((int)0x80040003); }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Formats(FORMATETC[] items) : IEnumFORMATETC
    {
        private int _position;
        public int Next(int count, FORMATETC[] result, int[]? fetched)
        {
            var copied = 0;
            while (copied < count && _position < items.Length) result[copied++] = items[_position++];
            if (fetched is { Length: > 0 }) fetched[0] = copied;
            return copied == count ? 0 : 1;
        }
        public int Skip(int count) { var available = items.Length - _position; _position += Math.Min(count, available); return available >= count ? 0 : 1; }
        public int Reset() { _position = 0; return 0; }
        public void Clone(out IEnumFORMATETC clone) => clone = new Formats(items) { _position = _position };
    }

    /// <summary>Disk-backed seekable COM stream; reads pull bounded chunks, never buffer a whole file in RAM.</summary>
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class ContentStream(long length, Func<CancellationToken, Stream> open, Func<bool> valid, Func<ClipboardFileTransfer?>? beginTransfer = null) : IStream, IDisposable
    {
        private Stream? _source;
        private ClipboardFileTransfer? _transfer;
        private bool _started;
        private bool _finished;
        private bool _disposed;
        private CancellationToken Token => _transfer?.CancellationToken ?? CancellationToken.None;
        private void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started) { _started = true; _transfer = beginTransfer?.Invoke(); }
            Token.ThrowIfCancellationRequested();
        }
        private void Finish(Exception? error)
        {
            if (_finished) return;
            _finished = true;
            _transfer?.Complete(error);
        }
        public void EndOperation(int result)
        {
            lock (_gate)
            {
                if (_started && !_finished) Finish(result is unchecked((int)0x800704C7) or unchecked((int)0x80004004) || result >= 0
                    ? new OperationCanceledException() : Marshal.GetExceptionForHR(result));
                Dispose();
            }
        }
        private FileStream? _cache;
        private long _position;
        private readonly object _gate = new();
        private void Ensure(long end)
        {
            if (!valid()) throw new IOException("The source session has changed.");
            _cache ??= new FileStream(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "relaxkonos-clipboard-" + Guid.NewGuid().ToString("N")),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, FileOptions.DeleteOnClose);
            if (_cache.Length >= end) return;
            _source ??= open(Token);
            var buffer = new byte[131072];
            _cache.Position = _cache.Length;
            while (_cache.Length < end)
            {
                if (!valid()) throw new IOException("The source session has changed.");
                Token.ThrowIfCancellationRequested();
                var count = _source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - _cache.Length)), Token)
                    .AsTask().GetAwaiter().GetResult();
                if (count == 0) throw new EndOfStreamException("The source file changed or the transfer was interrupted.");
                _cache.Write(buffer, 0, count);
                _transfer?.Report(_cache.Length);
            }
            if (_cache.Length == length) { _source.Dispose(); _source = null; }
        }
        public void Read(byte[] buffer, int count, IntPtr read)
        {
            lock (_gate)
            {
                try
                {
                    Start();
                    var wanted = (int)Math.Min(count, Math.Max(0, length - _position));
                    Ensure(_position + wanted);
                    _cache!.Position = _position;
                    var received = _cache.Read(buffer, 0, wanted);
                    _position += received;
                    if (read != IntPtr.Zero) Marshal.WriteInt32(read, received);
                    if (_position >= length) Finish(null);
                }
                catch (Exception ex) { Finish(ex); Dispose(); throw; }
            }
        }
        public void Seek(long offset, int origin, IntPtr position)
        {
            lock (_gate)
            {
                var next = checked((origin switch { 0 => 0, 1 => _position, 2 => length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset);
                if (next < 0) throw new IOException("Invalid stream position.");
                _position = next;
                if (position != IntPtr.Zero) Marshal.WriteInt64(position, next);
            }
        }
        public void Stat(out STATSTG stat, int flags) => stat = new() { type = 2, cbSize = length, grfMode = 0 };
        public void CopyTo(IStream target, long count, IntPtr read, IntPtr written)
        {
            long total = 0;
            var buffer = new byte[131072];
            var actual = Marshal.AllocCoTaskMem(4);
            try
            {
                while (total < count)
                {
                    Read(buffer, (int)Math.Min(buffer.Length, count - total), actual);
                    var received = Marshal.ReadInt32(actual);
                    if (received == 0) break;
                    target.Write(buffer, received, actual);
                    if (Marshal.ReadInt32(actual) != received) throw new IOException("Incomplete target write.");
                    total += received;
                }
            }
            finally { Marshal.FreeCoTaskMem(actual); if (read != IntPtr.Zero) Marshal.WriteInt64(read, total); if (written != IntPtr.Zero) Marshal.WriteInt64(written, total); }
        }
        public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; if (_started && !_finished) Finish(new OperationCanceledException()); _source?.Dispose(); _cache?.Dispose(); _source = null; _cache = null; } GC.SuppressFinalize(this); }
        ~ContentStream() { try { if (_started && !_finished) Finish(new OperationCanceledException()); _source?.Dispose(); _cache?.Dispose(); } catch { /* Finalization must not terminate the client. */ } }
        public void Write(byte[] b, int n, IntPtr p) => throw new NotSupportedException();
        public void SetSize(long size) => throw new NotSupportedException();
        public void Commit(int flags) { }
        public void Revert() => throw new NotSupportedException();
        public void LockRegion(long offset, long count, int type) => throw new NotSupportedException();
        public void UnlockRegion(long offset, long count, int type) => throw new NotSupportedException();
        public void Clone(out IStream stream) { lock (_gate) stream = new ContentStream(length, open, valid, beginTransfer) { _position = _position }; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Message { public IntPtr hwnd; public uint msg; public UIntPtr wparam; public IntPtr lparam; public uint time; public int x, y; public uint privateData; }
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern int OleSetClipboard(IDataObject data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr handle);
}
