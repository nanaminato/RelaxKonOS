using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using RelaxKonOS.Client.Apps.Explorer.Models;

namespace RelaxKonOS.Client.Apps.Explorer;

public sealed record HostFileClipboardSnapshot(bool IsRemoteCopy, IReadOnlyList<LocalUploadSource> Files);

/// <summary>Tracks which file clipboard was copied most recently across the client and host OS.</summary>
public static class HostFileClipboard
{
    private static readonly DataFormat<string> RemoteCopyFormat =
        DataFormat.CreateStringApplicationFormat("relaxkonos.remote-file-copy");
    private static readonly string ProcessMarker = Guid.NewGuid().ToString("N");

    public static async Task MarkRemoteCopyAsync(IClipboard? clipboard)
    {
        if (clipboard is null) return;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(RemoteCopyFormat, ProcessMarker));
        await clipboard.SetDataAsync(data);
    }

    public static async Task<HostFileClipboardSnapshot> ReadAsync(IClipboard? clipboard)
    {
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
