using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.CodeEditor;

/// <summary>In-memory state for one editor tab. Content is never persisted by the client.</summary>
public sealed partial class CodeEditorDocument : ObservableObject
{
    public CodeEditorDocument(string? path, string text, string encodingName, string untitledName)
    {
        Path = path;
        Text = text;
        EncodingName = encodingName;
        UntitledName = untitledName;
    }

    [ObservableProperty] private string? _path;
    [ObservableProperty] private string _text;
    [ObservableProperty] private string _encodingName;
    [ObservableProperty] private bool _isDirty;

    /// <summary>
    /// Bumped on every content change. A write snapshots this before it starts so that a
    /// successful save cannot clear the dirty flag of an edit made while it was in flight.
    /// </summary>
    public int Revision { get; private set; }

    public string UntitledName { get; }
    public string DisplayName => string.IsNullOrWhiteSpace(Path) ? UntitledName : System.IO.Path.GetFileName(Path) ?? UntitledName;

    /// <summary>
    /// The tab's file-type mark. An untitled document has no extension at all, so it falls to the
    /// generic file asset — the same classification the file browser's list makes for that name.
    /// </summary>
    public ExplorerFileIconKind Kind => ExplorerFileIconKindResolver.ForEntry(FileSystemEntryType.File, DisplayName);

    partial void OnPathChanged(string? value)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Kind));
    }

    partial void OnTextChanged(string value) => Revision++;
}
