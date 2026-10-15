using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Protocol.Files;

// Opt-in acceptance harness. Uses two processes and the real Windows OLE clipboard.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class WindowsClipboardAcceptance
{
    public static int Run(string mode)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { RunSta(mode); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (error is null) return 0;
        Console.Error.WriteLine(error); return 1;
    }
    private static void RunSta(string mode)
    {
        Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
        if (mode == "--clipboard-formats")
        {
            foreach (var name in new[] { "FileGroupDescriptorW", "FileGroupDescriptor", "FileContents", "Shell IDList Array", "Preferred DropEffect" })
                Console.WriteLine($"{name}: {unchecked((short)RegisterClipboardFormat(name))} available={IsClipboardFormatAvailable(RegisterClipboardFormat(name))}");
            return;
        }
        if (mode is "--clipboard-source" or "--clipboard-source-slow" or "--clipboard-source-tree")
        {
            AcceptanceClient.Slow = mode == "--clipboard-source-slow";
            AcceptanceClient.Tree = mode == "--clipboard-source-tree";
            WindowsFileClipboard.Trace = message => { Console.WriteLine(message); Console.Out.Flush(); };
            var client = DispatchProxy.Create<IExplorerClient, AcceptanceClient>();
            HostFileClipboard.PublishAsync(null, client, AcceptanceClient.Tree
                ? [new("/tree", "剪贴板验收", null, FileSystemEntryType.Directory, null, null, null, false, false, null)]
                : [new("/acceptance.bin", AcceptanceClient.Slow ? "relaxkonos-progress.bin" : "relaxkonos-acceptance.bin", AcceptanceClient.Slow ? 32L * 1024 * 1024 : 262144, FileSystemEntryType.File, null, null, null, false, false, null)],
                () => "clipboard-acceptance-session", (path, size) => new ClipboardFileTransfer(
                    bytes => { if (bytes % (1024 * 1024) == 0) { Console.WriteLine($"PROGRESS {bytes}/{size}"); Console.Out.Flush(); } },
                    error => { Console.WriteLine("TRANSFER_END " + (error?.GetType().Name ?? "success")); Console.Out.Flush(); }, CancellationToken.None)).GetAwaiter().GetResult();
            if (!WindowsFileClipboard.IsCurrent) throw new IOException("Published file clipboard does not own paste.");
            Console.WriteLine("CLIPBOARD_READY"); Console.Out.Flush();
            var end = DateTime.UtcNow.AddSeconds(90);
            var owned = true;
            while (DateTime.UtcNow < end)
            {
                while (PeekMessage(out var message, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref message); DispatchMessage(ref message); }
                var current = WindowsFileClipboard.IsCurrent;
                if (owned != current) { owned = current; Console.WriteLine("CLIPBOARD_OWNERSHIP " + current); Console.Out.Flush(); }
                Thread.Sleep(10);
            }
            GC.KeepAlive(client);
        }
        else
        {
            Marshal.ThrowExceptionForHR(OleGetClipboard(out var data));
            if (data is WindowsFileClipboard.IDataObjectAsyncCapability asynchronous)
            {
                Marshal.ThrowExceptionForHR(asynchronous.GetAsyncMode(out var enabled));
                if (!enabled) throw new IOException("Shell asynchronous mode is disabled.");
                Marshal.ThrowExceptionForHR(asynchronous.StartOperation(null));
                Console.WriteLine("ASYNC_START_PASS");
            }
            else throw new IOException("Shell asynchronous capability is unavailable across processes.");
            var formats = data.EnumFormatEtc(DATADIR.DATADIR_GET);
            var entries = new FORMATETC[1]; var fetched = new int[1];
            while (formats.Next(1, entries, fetched) == 0) Console.WriteLine($"FORMAT {entries[0].cfFormat} {entries[0].tymed} {entries[0].lindex}");
            var f = new FORMATETC { cfFormat = unchecked((short)RegisterClipboardFormat("FileContents")), dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = 0, tymed = TYMED.TYMED_ISTREAM };
            var query = data.QueryGetData(ref f);
            Console.WriteLine($"QUERY {query:X8}");
            f.lindex = -1;
            var generic = data.QueryGetData(ref f);
            Console.WriteLine($"QUERY_GENERIC {generic:X8}");
            Marshal.ThrowExceptionForHR(generic);
            f.lindex = 0;
            data.GetData(ref f, out var medium);
            try
            {
                var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                stream.Stat(out var stat, 1);
                Console.WriteLine($"SIZE {stat.cbSize}");
                var buffer = new byte[65536]; var n = Marshal.AllocCoTaskMem(4); long total = 0;
                try
                {
                    while (true)
                    {
                        stream.Read(buffer, buffer.Length, n); var count = Marshal.ReadInt32(n);
                        if (count == 0) break;
                        for (var i = 0; i < count; i++) if (buffer[i] != (byte)(total + i)) throw new IOException($"Content mismatch at {total + i}");
                        total += count;
                    }
                    if (total != 262144) throw new IOException($"Length mismatch: {total}");
                    Console.WriteLine($"CROSS_PROCESS_PASS {total}");
                }
                finally { Marshal.FreeCoTaskMem(n); Marshal.ReleaseComObject(stream); }
            }
            finally { ReleaseStgMedium(ref medium); Marshal.ReleaseComObject(data); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public IntPtr hwnd; public uint msg; public UIntPtr wparam; public IntPtr lparam; public uint time; public int x, y; public uint privateData; }
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern int OleGetClipboard(out IDataObject data);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
}
public class AcceptanceClient : DispatchProxy
{
    public static bool Slow;
    public static bool Tree;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == nameof(IExplorerClient.GetDirectoryAsync))
            return Task.FromResult(new DirectoryDto((string)args![0]!, "剪贴板验收", FileSystemEntryType.Directory,
                (string)args[0]! == "/tree" ? [new("/tree/empty", "空目录", null, FileSystemEntryType.Directory, null, null, null, false, false, null)] : [],
                (string)args[0]! == "/tree" ? [new("/tree/unicode", "中文.bin", null, 262144, null, null, null, false, false, "application/octet-stream"),
                    new("/tree/zero", "空文件.txt", null, 0, null, null, null, false, false, "text/plain")] : [], null, null));
        if (method!.Name == nameof(IExplorerClient.DownloadAsync))
        {
            Console.WriteLine("CONTENT_OPENED"); Console.Out.Flush();
            return Task.FromResult<(Stream Stream, string FileName)?>((new GeneratedStream(Slow ? 32L * 1024 * 1024 : 262144, Slow), "relaxkonos-acceptance.bin"));
        }
        throw new NotSupportedException(method.Name);
    }
}

public sealed class GeneratedStream(long length, bool slow) : Stream
{
    private long _position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(byte[] bytes, int offset, int count)
    {
        var received = (int)Math.Min(count, length - _position);
        if (slow) Thread.Sleep(100);
        for (var i = 0; i < received; i++) bytes[offset + i] = (byte)(_position + i);
        _position += received;
        return received;
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long length) => throw new NotSupportedException();
    public override void Write(byte[] bytes, int offset, int count) => throw new NotSupportedException();
}
