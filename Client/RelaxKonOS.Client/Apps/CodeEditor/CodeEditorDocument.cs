using CommunityToolkit.Mvvm.ComponentModel;

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

    public string UntitledName { get; }
    public string DisplayName => string.IsNullOrWhiteSpace(Path) ? UntitledName : System.IO.Path.GetFileName(Path) ?? UntitledName;

    partial void OnPathChanged(string? value) => OnPropertyChanged(nameof(DisplayName));
}
