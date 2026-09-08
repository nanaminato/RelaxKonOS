using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RelaxKonOS.Client.Apps.CodeEditor;

/// <summary>Lazy node in a Code Editor workspace folder tree.</summary>
public sealed partial class CodeEditorFolderNode : ObservableObject
{
    private bool _isExpanded;

    public CodeEditorFolderNode(string name, string path, bool isDirectory, bool isWorkspaceRoot = false)
    {
        Name = name;
        Path = path;
        IsDirectory = isDirectory;
        IsWorkspaceRoot = isWorkspaceRoot;
        if (isDirectory) Children.Add(new CodeEditorFolderNode(string.Empty, string.Empty, false, isPlaceholder: true));
    }

    private CodeEditorFolderNode(string name, string path, bool isDirectory, bool isWorkspaceRoot = false, bool isPlaceholder = false)
    {
        Name = name;
        Path = path;
        IsDirectory = isDirectory;
        IsWorkspaceRoot = isWorkspaceRoot;
        IsPlaceholder = isPlaceholder;
    }

    public string Name { get; }
    public string Path { get; }
    public bool IsDirectory { get; }
    public bool IsWorkspaceRoot { get; }
    public bool IsPlaceholder { get; }
    public string Glyph => IsDirectory ? "📁" : "📄";
    public ObservableCollection<CodeEditorFolderNode> Children { get; } = [];
    public Func<CodeEditorFolderNode, Task>? ExpandRequested { get; set; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value) && value && IsDirectory && !IsLoaded)
                _ = ExpandRequested?.Invoke(this);
        }
    }

    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private bool _isLoading;
}
