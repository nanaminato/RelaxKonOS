using RelaxKonOS.Protocol.Files;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using RelaxKonOS.Client.Apps.Explorer.Models;

namespace RelaxKonOS.Client.Apps.Explorer;

public sealed record HostFileClipboardSnapshot(bool IsRemoteCopy, IReadOnlyList<LocalUploadSource> Files);

/// <summary>Tracks which file clipboard was copied most recently across the client and host OS.</summary>
public static class HostFileClipboard
{
    internal static readonly DataFormat<string> RemoteCopyFormat =
        DataFormat.CreateStringApplicationFormat("relaxkonos.remote-file-copy");
    private static readonly string ProcessMarker = Guid.NewGuid().ToString("N");
    private static readonly IHostFileClipboardBackend Backend = CreateBackend();

    private static IHostFileClipboardBackend CreateBackend()
    {
        if (OperatingSystem.IsWindows()) return new WindowsHostFileClipboardBackend();
        if (OperatingSystem.IsMacOS()) return new MacosHostFileClipboardBackend();
        if (OperatingSystem.IsLinux()) return new LinuxHostFileClipboardBackend();
        return new MarkerHostFileClipboardBackend();
    }

    public static Task PublishAsync(IClipboard? clipboard, IExplorerClient client,
        IReadOnlyList<FileSystemEntryDto> entries, Func<string?> sessionScope,
        Func<string, long, ClipboardFileTransfer> beginTransfer) =>
        Backend.PublishAsync(clipboard, client, entries, sessionScope, beginTransfer);

    public static async Task MarkRemoteCopyAsync(IClipboard? clipboard)
    {
        if (clipboard is null) return;
        await clipboard.SetDataAsync(CreateRemoteCopyData());
    }

    internal static DataTransfer CreateRemoteCopyData()
    {
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(RemoteCopyFormat, ProcessMarker));
        return data;
    }

    public static async Task<HostFileClipboardSnapshot> ReadAsync(IClipboard? clipboard)
    {
        if (Backend.IsCurrentRemoteCopy) return new(true, []);
        if (clipboard is null) return new(false, []);
        var transfer = await clipboard.TryGetDataAsync();
        if (transfer is null) return new(false, []);
        try
        {
            var marker = await transfer.TryGetValueAsync(RemoteCopyFormat);
            var files = await transfer.TryGetFilesAsync();
            return new(marker == ProcessMarker,
                files?.Select(file => file.TryGetLocalPath()).OfType<string>()
                    .Select(path => new LocalUploadSource(path)).ToArray() ?? []);
        }
        finally
        {
            if (transfer is IAsyncDisposable asynchronous) await asynchronous.DisposeAsync();
            else (transfer as IDisposable)?.Dispose();
        }
    }
}
