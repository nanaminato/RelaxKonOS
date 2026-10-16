using System.Text;
using Avalonia.Input;
using Avalonia.Input.Platform;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer;

/// <summary>File-manager interoperability over Avalonia's Linux clipboard transport.</summary>
internal sealed class LinuxHostFileClipboardBackend : IHostFileClipboardBackend
{
    internal static readonly DataFormat<byte[]> UriList = DataFormat.CreateBytesPlatformFormat("text/uri-list");
    internal static readonly DataFormat<byte[]> GnomeFiles = DataFormat.CreateBytesPlatformFormat("x-special/gnome-copied-files");
    internal static readonly DataFormat<byte[]> KdeCutSelection = DataFormat.CreateBytesPlatformFormat("application/x-kde-cutselection");
    private static readonly DataFormat<byte[]> Timestamp = DataFormat.CreateBytesPlatformFormat("TIMESTAMP");

    // Ownership is read from the process marker in HostFileClipboard.ReadAsync.
    public bool IsCurrentRemoteCopy => false;

    public async Task PublishAsync(IClipboard? clipboard, IExplorerClient client,
        IReadOnlyList<FileSystemEntryDto> entries, Func<string?> sessionScope,
        Func<string, long, ClipboardFileTransfer> beginTransfer)
    {
        if (clipboard is null) throw new IOException("The host clipboard is unavailable.");
        var scope = sessionScope();
        bool Valid() => scope is not null && scope == sessionScope();
        await HostFileClipboardStaging.Publication.WaitAsync();
        string? root = null;
        try
        {
            var owner = await clipboard.TryGetInProcessDataAsync();
            var stamp = await ReadStampAsync(clipboard);
            root = HostFileClipboardStaging.CreateRoot();
            var paths = await Task.Run(() => HostFileClipboardStaging.PrepareAsync(root, client, entries, Valid,
                beginTransfer, ValidateComponent, StringComparer.Ordinal, name => name));
            if (!Valid()) throw new IOException("The source session has changed.");
            if (!ReferenceEquals(owner, await clipboard.TryGetInProcessDataAsync()) || stamp != await ReadStampAsync(clipboard))
                throw new IOException("The host clipboard has changed.");
            if (!Valid()) throw new IOException("The source session has changed.");
            await clipboard.SetDataAsync(CreateData(paths));
            // Other processes may still be copying an older selection. Retain until exit.
            root = null;
        }
        finally
        {
            if (root is not null) HostFileClipboardStaging.Delete(root);
            HostFileClipboardStaging.Publication.Release();
        }
    }

    internal static DataTransfer CreateData(IReadOnlyList<string> paths)
    {
        var uris = paths.Select(path => new Uri(Path.GetFullPath(path)).AbsoluteUri).ToArray();
        var item = new DataTransferItem();
        item.Set(UriList, Encoding.UTF8.GetBytes(string.Join("\r\n", uris) + "\r\n"));
        item.Set(GnomeFiles, Encoding.UTF8.GetBytes("copy\n" + string.Join("\n", uris)));
        item.Set(KdeCutSelection, new byte[] { (byte)'0' });
        var data = HostFileClipboard.CreateRemoteCopyData();
        data.Add(item);
        return data;
    }

    internal static void ValidateComponent(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(['/', '\0']) >= 0
            || Encoding.UTF8.GetByteCount(name) > 255)
            throw new IOException("A source name cannot be represented by Linux: " + name);
    }

    // Avoid overwriting a later host copy during a long download. X11's TIMESTAMP also
    // distinguishes equal-content selections. Transports without it use known payloads.
    private static async Task<string> ReadStampAsync(IClipboard clipboard)
    {
        var transfer = await clipboard.TryGetDataAsync();
        if (transfer is null) return "";
        try
        {
            var parts = transfer.Formats.Select(format => (string?)format.ToString()).OrderBy(name => name, StringComparer.Ordinal).ToList();
            parts.Add(await transfer.TryGetValueAsync(DataFormat.Text));
            parts.Add(await transfer.TryGetValueAsync(HostFileClipboard.RemoteCopyFormat));
            foreach (var format in new[] { Timestamp, UriList, GnomeFiles, KdeCutSelection })
            {
                var value = await transfer.TryGetValueAsync(format);
                parts.Add(value is null ? null : Convert.ToBase64String(value));
            }
            return System.Text.Json.JsonSerializer.Serialize(parts);
        }
        finally
        {
            if (transfer is IAsyncDisposable asynchronous) await asynchronous.DisposeAsync();
            else (transfer as IDisposable)?.Dispose();
        }
    }
}
