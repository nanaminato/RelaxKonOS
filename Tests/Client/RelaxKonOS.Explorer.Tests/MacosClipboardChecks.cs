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
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name == nameof(IExplorerClient.DownloadAsync)
        ? Task.FromResult<(Stream Stream, string FileName)?>((new MemoryStream([1, 2, 3]), "../../ignored"))
        : throw new NotSupportedException(method.Name);
}

