using System.Reflection;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Protocol.Files;

public static class MacosClipboardChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var name in new[] { "..", "a/b", "a:b", "a\0b", "a\\b", new string('中', 86) })
        {
            try { MacosHostFileClipboardBackend.ValidateComponent(name); check(false, "Reject unsafe macOS name"); }
            catch (IOException) { check(true, "Reject unsafe macOS name"); }
        }
        MacosHostFileClipboardBackend.ValidateComponent("中文 😀.txt");
        var client = DispatchProxy.Create<IExplorerClient, MacosDownloadFake>();
        var root = Path.Combine(Path.GetTempPath(), "clipboard-test-" + Guid.NewGuid().ToString("N"));
        FileSystemEntryDto Entry(string name, long size = 3) => new("/source", name, size, FileSystemEntryType.File, null, null, null, false, false, null);
        IReadOnlyList<MacosHostFileClipboardBackend.Promise> published = [];
        long count = 7;
        string? scope = "session-one";
        int started = 0;
        var backend = new MacosHostFileClipboardBackend(() => count, promises => { published = promises; return ++count; },
            action => { action(); return Task.CompletedTask; });
        await backend.PublishAsync(null, client, [Entry("lazy.txt")], () => scope,
            (_, _) => { started++; return new(_ => { }, _ => { }, CancellationToken.None); });
        check(published.Count == 1 && started == 0 && ((MacosDownloadFake)(object)client).Downloads == 0,
            "macOS publishes promises without downloading or starting transfers");
        check(backend.IsCurrentRemoteCopy, "macOS promise publication tracks pasteboard ownership");
        count++;
        check(!backend.IsCurrentRemoteCopy, "macOS external pasteboard replacement clears remote ownership");
        Directory.CreateDirectory(root);
        try
        {
            var destination = Path.Combine(root, "Finder renamed.txt");
            await published[0].FulfillAsync(destination);
            check(started == 1 && File.ReadAllBytes(destination).SequenceEqual(new byte[] { 1, 2, 3 }),
                "macOS promise downloads only on request and uses Finder's destination name");
            await published[0].FulfillAsync(Path.Combine(root, "second paste.txt"));
            check(started == 2, "macOS supports repeated promise fulfillment after clipboard replacement");
            try { await published[0].FulfillAsync(destination); check(false, "macOS promise conflict"); }
            catch (IOException) { check(File.ReadAllBytes(destination).Length == 3, "macOS promise never overwrites existing destinations"); }
            var observed = new List<Exception?>();
            var shortPromise = new MacosHostFileClipboardBackend.Promise(Entry("short", 4), client, () => true,
                (_, _) => new(_ => { }, observed.Add, CancellationToken.None));
            try { await shortPromise.FulfillAsync(Path.Combine(root, "short.txt")); check(false, "macOS short promise"); }
            catch (EndOfStreamException) { check(observed.Count == 1 && observed[0] is EndOfStreamException
                && !File.Exists(Path.Combine(root, "short.txt")), "macOS promise reports short downloads once and removes partial output"); }
            observed.Clear();
            using var cancellation = new CancellationTokenSource();
            var cancelled = new MacosHostFileClipboardBackend.Promise(Entry("cancelled"), client, () => true,
                (_, _) => new(_ => { }, observed.Add, cancellation.Token));
            cancellation.Cancel();
            try { await cancelled.FulfillAsync(Path.Combine(root, "cancelled")); check(false, "macOS cancelled promise"); }
            catch (OperationCanceledException) { check(observed.Count == 1 && observed[0] is OperationCanceledException,
                "macOS promise propagates operation-center cancellation exactly once"); }
            observed.Clear();
            var coordinated = new MacosHostFileClipboardBackend.Promise(Entry("coordinated"), client, () => true,
                (_, _) => new(_ => { }, observed.Add, CancellationToken.None));
            try { await coordinated.FulfillAsync(Path.Combine(root, "coordinated"), _ => throw new IOException("Denied"));
                check(false, "macOS coordination failure"); }
            catch (IOException) { check(observed.Count == 1 && observed[0] is IOException
                && !File.Exists(Path.Combine(root, "coordinated")), "macOS coordinated write failure fails transfer before reporting success"); }
            var directoryClient = DispatchProxy.Create<IExplorerClient, LinuxClipboardClient>();
            var directory = new MacosHostFileClipboardBackend.Promise(Entry("folder") with { Type = FileSystemEntryType.Directory },
                directoryClient, () => true, (_, _) => new(_ => { }, _ => { }, CancellationToken.None));
            await directory.FulfillAsync(Path.Combine(root, "folder"));
            check(Directory.Exists(Path.Combine(root, "folder", "empty")) && new FileInfo(Path.Combine(root, "folder", "zero")).Length == 0,
                "macOS directory promises preserve empty directories and empty files");
            Directory.Delete(Path.Combine(root, "folder"), true);
            scope = "session-two";
            var before = started;
            try { await published[0].FulfillAsync(Path.Combine(root, "expired.txt")); check(false, "macOS expired promise"); }
            catch (IOException) { check(started == before, "macOS promise captures session and rejects later sessions before download"); }
            check(!Directory.EnumerateDirectories(root).Any(), "macOS promises clean private staging directories");
            scope = "session-one";
            try { await backend.PublishAsync(null, client, [Entry("é"), Entry("e\u0301")], () => scope,
                (_, _) => throw new Exception("Must remain lazy")); check(false, "macOS promise duplicate"); }
            catch (IOException) { check(published.Count == 1, "macOS promise rejects normalized duplicate names before replacing clipboard"); }
        }
        finally { Directory.Delete(root, true); }
        int completed = 0; long progress = 0; Exception? failure = null;
        ClipboardFileTransfer Begin(string path, long size) => new(bytes => progress = bytes, ex => { completed++; failure = ex; }, CancellationToken.None);
        try
        {
            var files = await PrepareAsync(root, client, [Entry("中文.txt")], () => true, Begin);
            check(File.ReadAllBytes(files[0]).SequenceEqual(new byte[] { 1, 2, 3 }) && completed == 1 && failure is null && progress == 3,
                "macOS snapshot streams content and completes progress");
            Directory.Delete(root, true);
            try { await PrepareAsync(root, client, [Entry("é"), Entry("e\u0301")], () => true, Begin); check(false, "macOS normalization collision"); }
            catch (IOException) { check(!Directory.Exists(root), "macOS normalization collision cleans snapshot"); }
            try { await PrepareAsync(root, client, [Entry("short", 4)], () => true, Begin); check(false, "macOS short download"); }
            catch (EndOfStreamException) { check(failure is EndOfStreamException && !Directory.Exists(root), "macOS short download fails transfer and removes partial files"); }
            try { await PrepareAsync(root, client, [Entry("expired")], () => false, Begin); check(false, "macOS expired session"); }
            catch (IOException) { check(!Directory.Exists(root), "macOS expired session cannot prepare files"); }
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await PrepareAsync(root, client, [Entry("cancelled")], () => true,
                (_, _) => new(_ => { }, ex => failure = ex, cancellation.Token)); check(false, "macOS cancellation"); }
            catch (OperationCanceledException) { check(failure is OperationCanceledException && !Directory.Exists(root), "macOS cancellation completes and cleans snapshot"); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static Task<IReadOnlyList<string>> PrepareAsync(string root, IExplorerClient client,
        IReadOnlyList<FileSystemEntryDto> entries, Func<bool> valid, Func<string, long, ClipboardFileTransfer> begin) =>
        HostFileClipboardStaging.PrepareAsync(root, client, entries, valid, begin,
            MacosHostFileClipboardBackend.ValidateComponent, StringComparer.OrdinalIgnoreCase,
            name => name.Normalize(System.Text.NormalizationForm.FormD));

}
public class MacosDownloadFake : DispatchProxy
{
    public int Downloads { get; private set; }
    private Task<(Stream Stream, string FileName)?> Download() { Downloads++; return Task.FromResult<(Stream Stream, string FileName)?>((new MemoryStream([1, 2, 3]), "../../ignored")); }
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name == nameof(IExplorerClient.DownloadAsync)
        ? Download()
        : throw new NotSupportedException(method.Name);
}
