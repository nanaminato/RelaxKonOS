using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Protocol.Files;
using Avalonia.Media;
using RelaxKonOS.Client.Controls.Diff;
using AvaloniaEdit.Document;

namespace RelaxKonOS.Client.Apps.CodeEditor;

/// <summary>In-memory state for one editor tab. Content is never persisted by the client.</summary>
public sealed partial class CodeEditorDocument : ObservableObject, IDisposable
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
    [ObservableProperty] private CodeEditorContentKind _contentKind;
    [ObservableProperty] private IImage? _imageSource;
    [ObservableProperty] private long _byteLength;
    [ObservableProperty] private bool _hasExternalChanges;
    [ObservableProperty] private bool _isReadOnly;
    public string? Version { get; set; }
    public string? ComparisonKey { get; init; }
    public DiffDocument? Comparison { get; set; }
    public string? ComparisonNoticeKey { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string? _tabTitle;
    public TextDocument? EditorBuffer { get; set; }
    public int CaretOffset { get; set; }
    public double VerticalOffset { get; set; }
    public double HorizontalOffset { get; set; }
    public bool ShowSource { get; set; }

    public void Dispose() => (ImageSource as IDisposable)?.Dispose();

    /// <summary>
    /// Bumped on every content change. A write snapshots this before it starts so that a
    /// successful save cannot clear the dirty flag of an edit made while it was in flight.
    /// </summary>
    public int Revision { get; private set; }

    public string UntitledName { get; }
    public string DisplayName => TabTitle ?? (string.IsNullOrWhiteSpace(Path) ? UntitledName
        : Path.Replace('\\', '/').Split('/').LastOrDefault() ?? UntitledName);

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

public enum CodeEditorContentKind { Text, Image, Binary, Comparison }
