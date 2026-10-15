using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using RelaxKonOS.Client.Apps.Explorer;

public static class WindowsFileClipboardChecks
{
    public static void Run(Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindows()) return;
        var opened = 0;
        var valid = true;
        var bytes = Enumerable.Range(0, 200000).Select(i => (byte)i).ToArray();
        var stream = new WindowsFileClipboard.ContentStream(bytes.Length,
            _ => { opened++; return new MemoryStream(bytes); }, () => valid);
        check(opened == 0, "Publishing a virtual stream does not read file content");
        stream.Stat(out var stat, 1);
        check(stat.cbSize == bytes.Length && opened == 0, "Shell metadata does not trigger a download");
        var buffer = new byte[100];
        var count = Marshal.AllocCoTaskMem(8);
        try
        {
            stream.Read(buffer, buffer.Length, count);
            check(opened == 1 && Marshal.ReadInt32(count) == 100 && buffer.SequenceEqual(bytes.Take(100)), "Shell reads stream the requested content");
            stream.Seek(-50, 1, count);
            stream.Read(buffer, 100, count);
            check(opened == 1 && buffer.SequenceEqual(bytes.Skip(50).Take(100)), "Backward seeks use the disk cache without reopening the network stream");
            stream.Seek(-10, 2, count);
            stream.Read(buffer, 100, count);
            check(Marshal.ReadInt32(count) == 10 && buffer.Take(10).SequenceEqual(bytes.TakeLast(10)), "End-relative seeks and short final reads preserve content");
            stream.Read(buffer, 100, count);
            check(Marshal.ReadInt32(count) == 0, "Shell receives EOF at the declared file size");
            valid = false;
            try { stream.Seek(0, 0, count); stream.Read(buffer, 1, count); check(false, "Session change must refuse reads"); }
            catch (IOException) { check(true, "Session changes refuse cached and remote content"); }
        }
        finally { Marshal.FreeCoTaskMem(count); stream.Dispose(); }
        long reported = 0;
        Exception? failure = null;
        var completed = 0;
        using var cancellation = new CancellationTokenSource();
        using (var observed = new WindowsFileClipboard.ContentStream(200000, _ => new MemoryStream(bytes), () => true,
            () => new ClipboardFileTransfer(n => reported = n, ex => { failure = ex; completed++; }, cancellation.Token)))
        {
            observed.Read(new byte[1000], 1000, IntPtr.Zero);
            check(reported == 1000 && completed == 0, "Clipboard progress reports actual bytes without premature completion");
            cancellation.Cancel();
            try { observed.Read(new byte[1000], 1000, IntPtr.Zero); check(false, "Cancelled clipboard reads must stop"); }
            catch (OperationCanceledException) { check(completed == 1 && failure is OperationCanceledException, "Cancellation stops content and completes progress exactly once"); }
        }
        completed = 0;
        using (var observed = new WindowsFileClipboard.ContentStream(0, _ => throw new Exception("Empty file should not open network"), () => true,
            () => new ClipboardFileTransfer(_ => { }, ex => { failure = ex; completed++; }, CancellationToken.None)))
        {
            observed.Read(new byte[1], 1, IntPtr.Zero);
            check(completed == 1 && failure is null, "Empty clipboard files finish without a network request");
        }
        foreach (var name in new[] { "..\\escape", "CON.txt", "folder\\a:b", "trailing.", "folder\\..\\escape" })
        {
            try { WindowsFileClipboard.ValidateName(name); check(false, "Invalid Windows name must be rejected"); }
            catch (IOException) { check(true, "Unsafe virtual file name rejected: " + name); }
        }
        try { WindowsFileClipboard.ValidateComponent("back\\slash"); check(false, "POSIX backslash must not become a directory separator"); }
        catch (IOException) { check(true, "POSIX backslash names cannot silently change the target directory tree"); }
        WindowsFileClipboard.ValidateName("目录\\empty.txt");
        var client = DispatchProxy.Create<IExplorerClient, ClipboardClientFake>();
        var data = new WindowsFileClipboard.VirtualFiles([
            new("/dir", "目录", true, 0), new("/dir/large", "目录\\large.bin", false, (1L << 32) + 7)], client, () => true);
        var format = new FORMATETC { cfFormat = unchecked((short)RegisterClipboardFormat("FileGroupDescriptorW")), dwAspect = DVASPECT.DVASPECT_CONTENT, tymed = TYMED.TYMED_HGLOBAL, lindex = -1 };
        data.GetData(ref format, out var medium);
        try
        {
            var p = GlobalLock(medium.unionmember);
            try
            {
                check((Marshal.ReadInt32(p, 4) & 0x4000) != 0, "Virtual files request the native Shell progress dialog");
                check(Marshal.ReadInt32(p) == 2, "Shell descriptor includes directories and their contents");
                check(Marshal.ReadInt32(p, 4 + 36) == 0x10, "Empty directories carry the directory attribute");
                check(Marshal.ReadInt32(p, 4 + 592 + 64) == 1 && Marshal.ReadInt32(p, 4 + 592 + 68) == 7,
                    "Shell descriptor preserves file sizes larger than 4 GiB");
                check(Marshal.PtrToStringUni(p + 4 + 592 + 72) == "目录\\large.bin", "Shell descriptor preserves Unicode relative paths");
            }
            finally { GlobalUnlock(medium.unionmember); }
        }
        finally { ReleaseStgMedium(ref medium); }
        format.cfFormat = unchecked((short)RegisterClipboardFormat("FileContents")); format.tymed = TYMED.TYMED_ISTREAM; format.lindex = 1;
        check(data.QueryGetData(ref format) == 0, "File content is addressed by descriptor index");
        data.GetData(ref format, out medium);
        ReleaseStgMedium(ref medium);
        check(((ClipboardClientFake)(object)client).Calls == 0, "Requesting a content handle still does not download bytes");
        format.lindex = -1;
        check(data.QueryGetData(ref format) == 0, "Shell can probe FileContents without a specific file index");
        format.lindex = 1;
        format.dwAspect = DVASPECT.DVASPECT_CONTENT | DVASPECT.DVASPECT_THUMBNAIL;
        check(data.QueryGetData(ref format) == 0, "Shell content queries accept a combined content and thumbnail aspect mask");
        format.dwAspect = DVASPECT.DVASPECT_CONTENT;
        format.lindex = 0;
        check(data.QueryGetData(ref format) < 0, "Directories cannot be read as file streams");
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}
public class ClipboardClientFake : DispatchProxy
{
    public int Calls;
    protected override object? Invoke(MethodInfo? method, object?[]? args) { Calls++; throw new NotSupportedException(); }
}
