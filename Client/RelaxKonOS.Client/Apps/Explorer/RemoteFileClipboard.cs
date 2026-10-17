using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer;

/// <summary>
/// Process-wide source selection for server-side copy and move. The host clipboard determines
/// whether this selection still owns paste; Windows exposes virtual files; macOS and Linux publish prepared file URLs.
/// </summary>
public interface IRemoteFileClipboard
{
    IReadOnlyList<FileSystemEntryDto> Entries { get; }
    RemoteFileClipboardOperation Operation { get; }
    bool HasEntries { get; }
    event EventHandler? Changed;

    void Set(IReadOnlyList<FileSystemEntryDto> entries, RemoteFileClipboardOperation operation);
    void Clear();
}

public enum RemoteFileClipboardOperation { Copy, Cut }

public sealed class RemoteFileClipboard : IRemoteFileClipboard
{
    private IReadOnlyList<FileSystemEntryDto> _entries = Array.Empty<FileSystemEntryDto>();

    public IReadOnlyList<FileSystemEntryDto> Entries => _entries;
    public RemoteFileClipboardOperation Operation { get; private set; }
    public bool HasEntries => _entries.Count > 0;
    public event EventHandler? Changed;

    public void Set(IReadOnlyList<FileSystemEntryDto> entries, RemoteFileClipboardOperation operation)
    {
        _entries = entries.ToArray();
        Operation = operation;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (_entries.Count == 0) return;
        _entries = Array.Empty<FileSystemEntryDto>();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
