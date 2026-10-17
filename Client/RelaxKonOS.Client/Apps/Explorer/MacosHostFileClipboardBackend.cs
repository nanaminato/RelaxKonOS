using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer;

/// <summary>Publishes metadata immediately; Finder requests each file's contents on paste.</summary>
internal sealed class MacosHostFileClipboardBackend : IHostFileClipboardBackend
{
    private readonly Func<long> _readCount;
    private readonly Func<IReadOnlyList<Promise>, long> _publish;
    private readonly Func<Action, Task> _dispatch;
    private long? _changeCount;

    internal MacosHostFileClipboardBackend() : this(() => Native.ChangeCount, Native.Publish,
        async action => await Dispatcher.UIThread.InvokeAsync(action)) { }
    internal MacosHostFileClipboardBackend(Func<long> readCount, Func<IReadOnlyList<Promise>, long> publish,
        Func<Action, Task> dispatch) => (_readCount, _publish, _dispatch) = (readCount, publish, dispatch);

    public bool IsCurrentRemoteCopy => _changeCount is { } count && _readCount() == count;

    public async Task PublishAsync(IClipboard? clipboard, IExplorerClient client,
        IReadOnlyList<FileSystemEntryDto> entries, Func<string?> sessionScope,
        Func<string, long, ClipboardFileTransfer> beginTransfer)
    {
        var scope = sessionScope();
        bool Valid() => scope is not null && scope == sessionScope();
        if (!Valid()) throw new IOException("The source session has changed.");
        if (entries.Count == 0 || entries.Count > 100000) throw new IOException("Invalid clipboard selection size.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var promises = entries.Select(entry =>
        {
            ValidateComponent(entry.Name);
            if (!names.Add(entry.Name.Normalize(NormalizationForm.FormD)))
                throw new IOException("The selection contains duplicate host file names.");
            if (entry.Type == FileSystemEntryType.File && entry.Size < 0) throw new IOException("Invalid source file size.");
            return new Promise(entry, client, Valid, beginTransfer);
        }).ToArray();
        await _dispatch(() =>
        {
            if (!Valid()) throw new IOException("The source session has changed.");
            _changeCount = _publish(promises);
        });
    }

    internal sealed record Promise(FileSystemEntryDto Entry, IExplorerClient Client, Func<bool> Valid,
        Func<string, long, ClipboardFileTransfer> BeginTransfer)
    {
        // The destination is the complete file URL chosen by Finder, including collision renaming.
        // Stage beside it so the final rename is on the same volume and never overwrites a file.
        internal async Task FulfillAsync(string destination, Action<Action>? coordinate = null)
        {
            if (!Valid()) throw new IOException("The source session has changed.");
            var parent = Path.GetDirectoryName(Path.GetFullPath(destination))!;
            var root = Path.Combine(parent, ".relaxkonos-promise-" + Guid.NewGuid().ToString("N"));
            var transfers = new List<ClipboardFileTransfer>();
            ClipboardFileTransfer Begin(string path, long size)
            {
                var transfer = BeginTransfer(path, size);
                transfers.Add(transfer);
                // The promise succeeds only after its coordinated destination write succeeds.
                return transfer with { Complete = _ => { } };
            }
            try
            {
                var paths = await HostFileClipboardStaging.PrepareAsync(root, Client, [Entry], Valid,
                    Begin, ValidateComponent, StringComparer.OrdinalIgnoreCase,
                    name => name.Normalize(NormalizationForm.FormD)).ConfigureAwait(false);
                void Commit()
                {
                    if (!Valid()) throw new IOException("The source session has changed.");
                    foreach (var transfer in transfers) transfer.CancellationToken.ThrowIfCancellationRequested();
                    if (Entry.Type == FileSystemEntryType.File) File.Move(paths[0], destination);
                    else Directory.Move(paths[0], destination);
                }
                if (coordinate is null) Commit();
                else coordinate(Commit);
                foreach (var transfer in transfers) transfer.Complete(null);
            }
            catch (Exception ex)
            {
                foreach (var transfer in transfers) transfer.Complete(ex);
                throw;
            }
            finally { HostFileClipboardStaging.Delete(root); }
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
        private const string System = "/usr/lib/libSystem.B.dylib";
        private static readonly IntPtr AppKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        private static readonly IntPtr StackBlockClass = NativeLibrary.GetExport(NativeLibrary.Load(System), "_NSConcreteStackBlock");
        private static readonly ConcurrentDictionary<IntPtr, Promise> Promises = new();
        // Root reverse P/Invoke delegates for the lifetime of the registered Objective-C class.
        private static readonly NameCallback Name = GetName;
        private static readonly WriteCallback Write = WritePromise;
        private static readonly IntPtr PromiseQueue = Send(Send(Class("NSOperationQueue"), "alloc"), "init");
        private static readonly ObjectCallback Queue = (_, _, _) => PromiseQueue;
        private static readonly DeallocCallback Dealloc = ReleaseDelegate;
        private static readonly IntPtr DelegateClass = RegisterDelegate();
        private static IntPtr Send(IntPtr obj, string selector) => Message(obj, Selector(selector));
        private static IntPtr Board => Send(Class("NSPasteboard"), "generalPasteboard");
        internal static long ChangeCount => Integer(Board, Selector("changeCount"));
        private static IntPtr Text(string value) => String(Class("NSString"), Selector("stringWithUTF8String:"), value);
        private static string PathOf(IntPtr url) => Marshal.PtrToStringUTF8(Send(Send(url, "path"), "UTF8String"))
            ?? throw new IOException("Invalid promise destination URL.");

        private static IntPtr RegisterDelegate()
        {
            _ = AppKit;
            var cls = AllocateClass(Class("NSObject"), "RelaxKonOSFilePromiseDelegate", 0);
            if (cls == IntPtr.Zero) throw new InvalidOperationException("Cannot register file promise delegate.");
            AddProtocol(cls, GetProtocol("NSFilePromiseProviderDelegate"));
            AddMethod(cls, Selector("filePromiseProvider:fileNameForType:"), Marshal.GetFunctionPointerForDelegate(Name), "@@:@@");
            AddMethod(cls, Selector("filePromiseProvider:writePromiseToURL:completionHandler:"), Marshal.GetFunctionPointerForDelegate(Write), "v@:@@@");
            AddMethod(cls, Selector("operationQueueForFilePromiseProvider:"), Marshal.GetFunctionPointerForDelegate(Queue), "@@:@");
            AddMethod(cls, Selector("dealloc"), Marshal.GetFunctionPointerForDelegate(Dealloc), "v@:");
            RegisterClass(cls);
            return cls;
        }

        internal static long Publish(IReadOnlyList<Promise> promises)
        {
            var pool = Send(Send(Class("NSAutoreleasePool"), "alloc"), "init");
            try
            {
                var array = Send(Class("NSMutableArray"), "array");
                foreach (var promise in promises)
                {
                    var handler = Send(Send(DelegateClass, "alloc"), "init");
                    Promises[handler] = promise;
                    var provider = TwoArguments(Send(Class("NSFilePromiseProvider"), "alloc"),
                        Selector("initWithFileType:delegate:"),
                        Text(promise.Entry.Type == FileSystemEntryType.File ? "public.data" : "public.directory"), handler);
                    try
                    {
                        if (provider == IntPtr.Zero) throw new IOException("Cannot create a file promise.");
                        // NSFilePromiseProvider.delegate is weak. Tie its managed state to provider lifetime.
                        Associate(provider, DelegateClass, handler, 1 /* OBJC_ASSOCIATION_RETAIN_NONATOMIC */);
                        Argument(array, Selector("addObject:"), provider);
                    }
                    finally { Send(handler, "release"); if (provider != IntPtr.Zero) Send(provider, "release"); }
                }
                var board = Board;
                Integer(board, Selector("clearContents"));
                if (Boolean(board, Selector("writeObjects:"), array) == 0) throw new IOException("Cannot publish macOS file promises.");
                return Integer(board, Selector("changeCount"));
            }
            finally { Send(pool, "drain"); }
        }

        private static IntPtr GetName(IntPtr self, IntPtr selector, IntPtr provider, IntPtr type)
            => Promises.TryGetValue(self, out var promise) ? Text(promise.Entry.Name) : Text("unavailable");

        private static void WritePromise(IntPtr self, IntPtr selector, IntPtr provider, IntPtr url, IntPtr completion)
        {
            // Copy the escaping block and retain the URL before returning to AppKit. The Task owns
            // the promise state, so replacing the pasteboard cannot dispose an in-flight download.
            var block = CopyBlock(completion);
            Send(url, "retain");
            Promises.TryGetValue(self, out var promise);
            _ = Task.Run(async () =>
            {
                Exception? failure = null;
                try
                {
                    if (promise is null) throw new IOException("The file promise is unavailable.");
                    string destination;
                    var pathPool = Send(Send(Class("NSAutoreleasePool"), "alloc"), "init");
                    try { destination = PathOf(url); }
                    finally { Send(pathPool, "drain"); }
                    await promise.FulfillAsync(destination, commit => Coordinate(url, commit));
                }
                catch (Exception ex) { failure = ex; }
                var pool = Send(Send(Class("NSAutoreleasePool"), "alloc"), "init");
                try
                {
                    var error = failure is null ? IntPtr.Zero : Error(Class("NSError"), Selector("errorWithDomain:code:userInfo:"), Text("RelaxKonOS.FilePromise"), 1,
                        TwoArguments(Class("NSDictionary"), Selector("dictionaryWithObject:forKey:"), Text(failure.Message), Text("NSLocalizedDescription")));
                    var invoke = Marshal.ReadIntPtr(block, 2 * IntPtr.Size);
                    Marshal.GetDelegateForFunctionPointer<CompletionCallback>(invoke)(block, error);
                }
                finally { ReleaseBlock(block); Send(url, "release"); Send(pool, "drain"); }
            });
        }

        private static void Coordinate(IntPtr url, Action commit)
        {
            var pool = Send(Send(Class("NSAutoreleasePool"), "alloc"), "init");
            var coordinator = Argument(Send(Class("NSFileCoordinator"), "alloc"), Selector("initWithFilePresenter:"), IntPtr.Zero);
            Exception? failure = null;
            AccessorCallback accessor = (_, _) => { try { commit(); } catch (Exception ex) { failure = ex; } };
            var descriptor = Marshal.AllocHGlobal(2 * IntPtr.Size);
            var block = Marshal.AllocHGlobal(4 * IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(descriptor, IntPtr.Zero);
                Marshal.WriteIntPtr(descriptor, IntPtr.Size, (IntPtr)(4 * IntPtr.Size));
                Marshal.WriteIntPtr(block, StackBlockClass);
                Marshal.WriteInt64(block, IntPtr.Size, 0); // flags and reserved
                Marshal.WriteIntPtr(block, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(accessor));
                Marshal.WriteIntPtr(block, 3 * IntPtr.Size, descriptor);
                CoordinateWrite(coordinator, Selector("coordinateWritingItemAtURL:options:error:byAccessor:"), url, 0, out var error, block);
                GC.KeepAlive(accessor);
                if (failure is not null) throw failure;
                if (error != IntPtr.Zero) throw new IOException("macOS could not coordinate the promised file write.");
            }
            finally { Marshal.FreeHGlobal(block); Marshal.FreeHGlobal(descriptor); Send(coordinator, "release"); Send(pool, "drain"); }
        }

        private static void ReleaseDelegate(IntPtr self, IntPtr selector)
        {
            Promises.TryRemove(self, out _);
            var superclass = new Super { Receiver = self, Class = Class("NSObject") };
            SuperMessage(ref superclass, selector);
        }
        [StructLayout(LayoutKind.Sequential)] private struct Super { public IntPtr Receiver; public IntPtr Class; }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr NameCallback(IntPtr self, IntPtr selector, IntPtr provider, IntPtr type);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void WriteCallback(IntPtr self, IntPtr selector, IntPtr provider, IntPtr url, IntPtr completion);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ObjectCallback(IntPtr self, IntPtr selector, IntPtr provider);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DeallocCallback(IntPtr self, IntPtr selector);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CompletionCallback(IntPtr block, IntPtr error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AccessorCallback(IntPtr block, IntPtr url);
        [DllImport(ObjC, EntryPoint = "objc_getClass")] private static extern IntPtr Class(string name);
        [DllImport(ObjC, EntryPoint = "sel_registerName")] private static extern IntPtr Selector(string name);
        [DllImport(ObjC, EntryPoint = "objc_allocateClassPair")] private static extern IntPtr AllocateClass(IntPtr parent, string name, nuint extra);
        [DllImport(ObjC, EntryPoint = "objc_registerClassPair")] private static extern void RegisterClass(IntPtr cls);
        [DllImport(ObjC, EntryPoint = "objc_getProtocol")] private static extern IntPtr GetProtocol(string name);
        [DllImport(ObjC, EntryPoint = "class_addProtocol")] private static extern byte AddProtocol(IntPtr cls, IntPtr protocol);
        [DllImport(ObjC, EntryPoint = "class_addMethod")] private static extern byte AddMethod(IntPtr cls, IntPtr selector, IntPtr method, string types);
        [DllImport(ObjC, EntryPoint = "objc_setAssociatedObject")] private static extern void Associate(IntPtr obj, IntPtr key, IntPtr value, nuint policy);
        [DllImport(ObjC, EntryPoint = "objc_msgSendSuper")] private static extern void SuperMessage(ref Super obj, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Message(IntPtr obj, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long Integer(IntPtr obj, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Argument(IntPtr obj, IntPtr selector, IntPtr argument);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr TwoArguments(IntPtr obj, IntPtr selector, IntPtr first, IntPtr second);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte Boolean(IntPtr obj, IntPtr selector, IntPtr argument);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr String(IntPtr obj, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string argument);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Error(IntPtr obj, IntPtr selector, IntPtr domain, long code, IntPtr info);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void CoordinateWrite(IntPtr obj, IntPtr selector, IntPtr url, nuint options, out IntPtr error, IntPtr accessor);
        [DllImport(System, EntryPoint = "_Block_copy")] private static extern IntPtr CopyBlock(IntPtr block);
        [DllImport(System, EntryPoint = "_Block_release")] private static extern void ReleaseBlock(IntPtr block);
    }
}
