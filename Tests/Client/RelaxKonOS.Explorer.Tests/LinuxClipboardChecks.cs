using System.Reflection;
using System.Text;
using Avalonia.Input;
using Avalonia.Input.Platform;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Protocol.Files;

public static class LinuxClipboardChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var path = Path.Combine(Path.GetTempPath(), "中文 # %\n.txt");
        var data = LinuxHostFileClipboardBackend.CreateData([path]);
        var uriBytes = (await data.TryGetValueAsync(LinuxHostFileClipboardBackend.UriList))!;
        var uriList = Encoding.UTF8.GetString(uriBytes);
        var uri = new Uri(path).AbsoluteUri;
        check(uriList == uri + "\r\n" && uriList.Contains("%23") && uriList.Contains("%25") && uriList.Contains("%0A"),
            "Linux URI list escapes Unicode, separators and newline injection");
        check(Encoding.UTF8.GetString((await data.TryGetValueAsync(LinuxHostFileClipboardBackend.GnomeFiles))!) == "copy\n" + uri,
            "Linux publishes GNOME/Thunar copy format without trailing empty lines");
        check(Encoding.UTF8.GetString((await data.TryGetValueAsync(LinuxHostFileClipboardBackend.KdeCutSelection))!) == "0",
            "Linux publishes KDE copy semantics");
        LinuxHostFileClipboardBackend.ValidateComponent("a:b\\c\n😀");
        foreach (var name in new[] { "", ".", "..", "a/b", "a\0b", new string('中', 86) })
        {
            try { LinuxHostFileClipboardBackend.ValidateComponent(name); check(false, "Linux rejects unsafe names"); }
            catch (IOException) { check(true, "Linux rejects unsafe names"); }
        }
        var clipboard = DispatchProxy.Create<IClipboard, ClipboardFake>();
        var fake = (ClipboardFake)(object)clipboard;
        var client = DispatchProxy.Create<IExplorerClient, LinuxClipboardClient>();
        var remote = (LinuxClipboardClient)(object)client;
        FileSystemEntryDto Entry(string name, bool directory = false) => new("/source", name, 3,
            directory ? FileSystemEntryType.Directory : FileSystemEntryType.File, null, null, null, false, false, null);
        var backend = new LinuxHostFileClipboardBackend();
        var completions = 0;
        ClipboardFileTransfer Begin(string _, long size) => new(_ => { }, ex => { if (ex is null) completions++; }, CancellationToken.None);
        var roots = new HashSet<string>();
        async Task<string[]> PublishedPaths()
        {
            var published = await fake.Data!.TryGetValueAsync(LinuxHostFileClipboardBackend.UriList);
            var paths = Encoding.UTF8.GetString(published!).Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(value => new Uri(value).LocalPath).ToArray();
            foreach (var item in paths) roots.Add(Path.GetDirectoryName(item)!);
            return paths;
        }
        try
        {
            await backend.PublishAsync(clipboard, client, [Entry("first.bin")], () => "session", Begin);
            var first = (await PublishedPaths())[0];
            check(File.ReadAllBytes(first).SequenceEqual(new byte[] { 1, 2, 3 }) && completions == 1,
                "Linux backend publishes a complete download and completes operation progress");
            check((await HostFileClipboard.ReadAsync(clipboard)).IsRemoteCopy,
                "Linux payload preserves internal remote clipboard ownership");
            await backend.PublishAsync(clipboard, client, [Entry("folder", true)], () => "session", Begin);
            var folder = (await PublishedPaths())[0];
            check(Directory.Exists(Path.Combine(folder, "empty")) && new FileInfo(Path.Combine(folder, "zero")).Length == 0,
                "Linux backend snapshots recursive empty directories and zero-byte files");
            check(File.Exists(first), "Linux retains earlier snapshots for in-flight file-manager copies");
            var original = fake.Data;
            try { await backend.PublishAsync(clipboard, client, [Entry("expired")], () => null, Begin); check(false, "Linux expired session"); }
            catch (IOException) { check(ReferenceEquals(original, fake.Data), "Linux expired session preserves the previous clipboard"); }
            remote.OnDownload = () => { var replacement = new DataTransfer(); replacement.Add(DataTransferItem.CreateText("later host copy")); fake.Data = replacement; };
            try { await backend.PublishAsync(clipboard, client, [Entry("replaced")], () => "session", Begin); check(false, "Linux clipboard replacement"); }
            catch (IOException) { check(await fake.Data!.TryGetTextAsync() == "later host copy", "Linux download cannot overwrite a newer host copy"); }
            remote.OnDownload = null;
            var scope = "session";
            remote.OnDownload = () => scope = "new-session";
            try { await backend.PublishAsync(clipboard, client, [Entry("session-change")], () => scope, Begin); check(false, "Linux session change"); }
            catch (IOException) { check(!(await HostFileClipboard.ReadAsync(clipboard)).IsRemoteCopy, "Linux session change during download cannot publish stale content"); }
        }
        finally { foreach (var root in roots) HostFileClipboardStaging.Delete(root); }
    }
}
public class LinuxClipboardClient : DispatchProxy
{
    public Action? OnDownload;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == nameof(IExplorerClient.GetDirectoryAsync))
        {
            var path = (string)args![0]!;
            return Task.FromResult(new DirectoryDto(path, "folder", FileSystemEntryType.Directory,
                path == "/source" ? [new("/empty", "empty", null, FileSystemEntryType.Directory, null, null, null, false, false, null)] : [],
                path == "/source" ? [new("/zero", "zero", null, 0, null, null, null, false, false, "application/octet-stream")] : [], null, null));
        }
        if (method.Name == nameof(IExplorerClient.DownloadAsync))
        {
            OnDownload?.Invoke();
            return Task.FromResult<(Stream Stream, string FileName)?>((new MemoryStream((string)args![0]! == "/zero" ? [] : [1, 2, 3]), "ignored"));
        }
        throw new NotSupportedException(method.Name);
    }
}
