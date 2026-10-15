using Avalonia.Input.Platform;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer;

/// <summary>
/// Platform boundary for publishing remote files to the host clipboard.
/// Reading ordinary host files remains on Avalonia's cross-platform clipboard API.
/// </summary>
internal interface IHostFileClipboardBackend
{
    bool IsCurrentRemoteCopy { get; }

    Task PublishAsync(IClipboard? clipboard, IExplorerClient client,
        IReadOnlyList<FileSystemEntryDto> entries, Func<string?> sessionScope,
        Func<string, long, ClipboardFileTransfer> beginTransfer);
}

internal sealed class WindowsHostFileClipboardBackend : IHostFileClipboardBackend
{
    public bool IsCurrentRemoteCopy => WindowsFileClipboard.IsCurrent;

    public async Task PublishAsync(IClipboard? clipboard, IExplorerClient client,
        IReadOnlyList<FileSystemEntryDto> entries, Func<string?> sessionScope,
        Func<string, long, ClipboardFileTransfer> beginTransfer)
    {
        var scope = sessionScope();
        await WindowsFileClipboard.PublishAsync(client, entries,
            () => scope is not null && scope == sessionScope(), beginTransfer);
    }
}

/// <summary>
/// Safe fallback for platforms where native virtual-file publication is not implemented yet.
/// It preserves RelaxKonOS server-side copy ownership without pretending a remote path is a local file.
/// </summary>
internal sealed class MarkerHostFileClipboardBackend : IHostFileClipboardBackend
{
    public bool IsCurrentRemoteCopy => false;

    public Task PublishAsync(IClipboard? clipboard, IExplorerClient client,
        IReadOnlyList<FileSystemEntryDto> entries, Func<string?> sessionScope,
        Func<string, long, ClipboardFileTransfer> beginTransfer) => HostFileClipboard.MarkRemoteCopyAsync(clipboard);
}
