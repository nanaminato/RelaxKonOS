using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer;

/// <summary>Finder consumes real file URLs. Prepare a private snapshot before publishing it.</summary>
internal sealed class MacosHostFileClipboardBackend : IHostFileClipboardBackend
{
    private long? _changeCount;
    public bool IsCurrentRemoteCopy => _changeCount is { } count && Native.ChangeCount == count;

    public async Task PublishAsync(IClipboard? clipboard, IExplorerClient client,
        IReadOnlyList<FileSystemEntryDto> entries, Func<string?> sessionScope,
        Func<string, long, ClipboardFileTransfer> beginTransfer)
    {
        var scope = sessionScope();
        bool Valid() => scope is not null && scope == sessionScope();
        await HostFileClipboardStaging.Publication.WaitAsync();
        string? root = null;
        try
        {
            var initialCount = Native.ChangeCount;
            root = HostFileClipboardStaging.CreateRoot();
            var paths = await Task.Run(() => HostFileClipboardStaging.PrepareAsync(root, client, entries, Valid, beginTransfer,
                ValidateComponent, StringComparer.OrdinalIgnoreCase, name => name.Normalize(NormalizationForm.FormD)));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!Valid()) throw new IOException("The source session has changed.");
                if (Native.ChangeCount != initialCount) throw new IOException("The host clipboard has changed.");
                _changeCount = Native.Publish(paths);
            });
            // Finder may still be reading an earlier copy after the pasteboard is replaced.
            // Keep successful snapshots until process exit; never delete them on replacement.
            root = null;
        }
        finally
        {
            if (root is not null) HostFileClipboardStaging.Delete(root);
            HostFileClipboardStaging.Publication.Release();
        }
    }

    internal static void ValidateComponent(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', ':', '\0']) >= 0
            || Encoding.UTF8.GetByteCount(name.Normalize(NormalizationForm.FormD)) > 255)
            throw new IOException("A source name cannot be represented by macOS: " + name);
    }

    private static class Native
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        // Load AppKit before looking up its Objective-C classes.
        private static readonly IntPtr AppKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        static Native() { _ = AppKit; }
        private static IntPtr Send(IntPtr obj, string selector) => Message(obj, Selector(selector));
        private static IntPtr Board => Send(Class("NSPasteboard"), "generalPasteboard");
        internal static long ChangeCount => Integer(Board, Selector("changeCount"));
        internal static long Publish(IReadOnlyList<string> paths)
        {
            var pool = Send(Send(Class("NSAutoreleasePool"), "alloc"), "init");
            try
            {
                var array = Send(Class("NSMutableArray"), "array");
                foreach (var path in paths)
                {
                    var text = String(Class("NSString"), Selector("stringWithUTF8String:"), path);
                    var url = Argument(Class("NSURL"), Selector("fileURLWithPath:"), text);
                    if (url == IntPtr.Zero) throw new IOException("Cannot create a file URL.");
                    Argument(array, Selector("addObject:"), url);
                }
                var board = Board;
                Integer(board, Selector("clearContents"));
                if (Boolean(board, Selector("writeObjects:"), array) == 0) throw new IOException("Cannot publish files to the macOS pasteboard.");
                return Integer(board, Selector("changeCount"));
            }
            finally { Send(pool, "drain"); }
        }
        [DllImport(ObjC, EntryPoint = "objc_getClass")] private static extern IntPtr Class(string name);
        [DllImport(ObjC, EntryPoint = "sel_registerName")] private static extern IntPtr Selector(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Message(IntPtr obj, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long Integer(IntPtr obj, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Argument(IntPtr obj, IntPtr selector, IntPtr argument);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte Boolean(IntPtr obj, IntPtr selector, IntPtr argument);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr String(IntPtr obj, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string argument);
    }
}
