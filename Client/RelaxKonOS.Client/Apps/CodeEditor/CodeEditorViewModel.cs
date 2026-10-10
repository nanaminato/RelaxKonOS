using System.Collections.ObjectModel;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.TextEditor;
using RelaxKonOS.Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.Client.Apps.CodeEditor;

/// <summary>Owns the window-local multi-root workspace and the currently open editor documents.</summary>
public sealed partial class CodeEditorViewModel : ObservableObject
{
    private readonly IExplorerClient? _files;
    private readonly StringComparer _pathComparer;
    private bool _isLoadingDocument;
    private int _untitledSequence;

    // Bumped for every read that will replace or add a document. A late read whose revision no
    // longer matches must not activate its file over whatever the user opened meanwhile.
    private int _openRevision;

    public CodeEditorViewModel(IExplorerClient? files, bool pathCaseSensitive = true, string defaultEncodingName = "UTF-8")
    {
        _files = files;
        _pathComparer = pathCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        DefaultEncodingName = TextFileEncodings.IsSupported(defaultEncodingName) ? defaultEncodingName : "UTF-8";
        EncodingName = DefaultEncodingName;
    }

    public ObservableCollection<CodeEditorFolderNode> WorkspaceRoots { get; } = [];
    public ObservableCollection<CodeEditorDocument> OpenDocuments { get; } = [];
    public IReadOnlyList<string> AvailableEncodings => TextFileEncodings.Available;
    public IReadOnlyList<double> FontSizes { get; } = [12, 13, 14, 16, 18, 20];

    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private string? _currentPath;
    [ObservableProperty] private string _encodingName = "UTF-8";
    [ObservableProperty] private string _defaultEncodingName = "UTF-8";
    [ObservableProperty] private double _fontSize = 14;
    [ObservableProperty] private bool _wordWrap;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private LocalizedStatus _statusText;
    [ObservableProperty] private CodeEditorDocument? _activeDocument;
    [ObservableProperty] private CodeEditorFolderNode? _selectedFolderNode;
    [ObservableProperty] private string _activeSidebar = "explorer";
    [ObservableProperty] private bool _isSidebarVisible = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditDocument))]
    private bool _isSaving;

    /// <summary>False while a write is in flight; the editor and its command surface bind to this.</summary>
    public bool CanEditDocument => !IsSaving;

    /// <summary>
    /// True while any open tab holds unsaved edits. The window close guard uses this rather than
    /// the active tab alone, so closing the window cannot silently drop a background document.
    /// </summary>
    public bool HasUnsavedDocuments => OpenDocuments.Any(document => document.IsDirty);

    public int CharCount => Text.Length;
    public int LineCount => string.IsNullOrEmpty(Text) ? 1 : Enumerable.Count<char>(Text, character => character == '\n') + 1;
    public string LineCountText => LocalizedText.Format("common.line_count_format", LineCount);
    public string CharacterCountText => LocalizedText.Format("common.character_count_format", CharCount);
    public string DocumentName => ActiveDocument?.DisplayName ?? LocalizedText.Get("code_editor.document.untitled");
    public bool IsExplorerSidebar => ActiveSidebar == "explorer";
    public bool IsOpenEditorsSidebar => ActiveSidebar == "openEditors";
    public bool HasOpenFile => !string.IsNullOrWhiteSpace(CurrentPath);

    public Func<Task<string?>>? RequestFileAsync { get; set; }
    public Func<Task<string?>>? RequestFolderAsync { get; set; }
    public Func<string, Task<string?>>? RequestSavePathAsync { get; set; }
    public Func<CodeEditorDocument, Task<bool>>? RequestDiscardChangesAsync { get; set; }
    public Func<Task>? RequestSettingsAsync { get; set; }
    public Func<Task<EncodingDialogAction?>>? RequestEncodingActionAsync { get; set; }
    public Func<Task<string?>>? RequestEncodingAsync { get; set; }
    public Action? CloseSettingsAction { get; set; }
    public Func<string, Task>? SaveDefaultEncodingAsync { get; set; }

    partial void OnTextChanged(string value)
    {
        OnPropertyChanged(nameof(CharCount));
        OnPropertyChanged(nameof(LineCount));
        OnPropertyChanged(nameof(LineCountText));
        OnPropertyChanged(nameof(CharacterCountText));
        if (ActiveDocument is null) return;
        ActiveDocument.Text = value;
        if (!_isLoadingDocument)
        {
            ActiveDocument.IsDirty = true;
            IsDirty = true;
        }
    }

    partial void OnCurrentPathChanged(string? value)
    {
        OnPropertyChanged(nameof(DocumentName));
        OnPropertyChanged(nameof(HasOpenFile));
    }
    partial void OnEncodingNameChanged(string value)
    {
        if (ActiveDocument is not null && !_isLoadingDocument) ActiveDocument.EncodingName = value;
    }

    partial void OnDefaultEncodingNameChanged(string value)
    {
        if (TextFileEncodings.IsSupported(value))
            _ = SaveDefaultEncodingAsync?.Invoke(value);
    }

    partial void OnActiveDocumentChanged(CodeEditorDocument? value)
    {
        _isLoadingDocument = true;
        Text = value?.Text ?? string.Empty;
        CurrentPath = value?.Path;
        EncodingName = value?.EncodingName ?? "UTF-8";
        IsDirty = value?.IsDirty ?? false;
        _isLoadingDocument = false;
        OnPropertyChanged(nameof(DocumentName));
    }

    partial void OnSelectedFolderNodeChanged(CodeEditorFolderNode? value)
    {
        if (value is null || value.IsPlaceholder) return;
        if (value.IsDirectory)
        {
            value.IsExpanded = true;
            return;
        }
        _ = OpenPathAsync(value.Path);
    }

    partial void OnActiveSidebarChanged(string value)
    {
        OnPropertyChanged(nameof(IsExplorerSidebar));
        OnPropertyChanged(nameof(IsOpenEditorsSidebar));
    }

    [RelayCommand]
    private void SwitchSidebar(string? sidebar)
    {
        if (sidebar is not ("explorer" or "openEditors")) return;

        // A second click on the active activity-bar item is a compact toggle, matching
        // the expected folder/open-files behavior while retaining the selected view.
        if (ActiveSidebar == sidebar && IsSidebarVisible)
        {
            IsSidebarVisible = false;
            return;
        }

        ActiveSidebar = sidebar;
        IsSidebarVisible = true;
    }

    [RelayCommand]
    private void NewDocument()
    {
        // A new tab supersedes any read still in flight.
        _openRevision++;
        var document = new CodeEditorDocument(null, string.Empty, DefaultEncodingName,
            LocalizedText.Format("code_editor.document.untitled_number", ++_untitledSequence));
        OpenDocuments.Add(document);
        ActiveDocument = document;
        StatusText = LocalizedText.Ref("code_editor.status.new_document");
    }

    [RelayCommand]
    private async Task OpenDocumentAsync()
    {
        if (IsSaving) return;
        var path = await (RequestFileAsync?.Invoke() ?? Task.FromResult<string?>(null));
        if (!string.IsNullOrWhiteSpace(path)) await OpenPathAsync(path);
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var path = await (RequestFolderAsync?.Invoke() ?? Task.FromResult<string?>(null));
        if (!string.IsNullOrWhiteSpace(path)) await AddWorkspaceRootAsync(path);
    }

    [RelayCommand]
    private async Task RefreshFolderAsync(CodeEditorFolderNode? node)
    {
        node ??= SelectedFolderNode;
        if (node is not null && node.IsDirectory) await LoadFolderAsync(node, force: true);
    }

    [RelayCommand]
    private void RemoveFolder(CodeEditorFolderNode? node)
    {
        node ??= SelectedFolderNode;
        if (node is null) return;
        var root = WorkspaceRoots.FirstOrDefault(item => ReferenceEquals(item, node) || IsAncestor(item, node));
        if (root is null) return;
        WorkspaceRoots.Remove(root);
        if (ReferenceEquals(SelectedFolderNode, node) || ReferenceEquals(root, node)) SelectedFolderNode = null;
        StatusText = LocalizedText.Ref("code_editor.status.folder_removed", root.Name);
    }

    [RelayCommand]
    private async Task CloseDocumentAsync(CodeEditorDocument? document)
    {
        if (IsSaving) return;
        document ??= ActiveDocument;
        if (document is null) return;
        if (document.IsDirty && !(await (RequestDiscardChangesAsync?.Invoke(document) ?? Task.FromResult(false)))) return;

        var index = OpenDocuments.IndexOf(document);
        OpenDocuments.Remove(document);
        if (!ReferenceEquals(ActiveDocument, document)) return;
        ActiveDocument = OpenDocuments.Count == 0 ? null : OpenDocuments[Math.Clamp(index, 0, OpenDocuments.Count - 1)];
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsSaving) return;
        if (ActiveDocument is null) NewDocument();
        var document = ActiveDocument;
        if (document is null) return;
        if (_files is null) { StatusText = LocalizedText.Ref("code_editor.status.connect_before_save"); return; }
        // Fix the document and its target before any picker opens, so the write cannot follow a
        // tab the user switched to while the save-path picker was up.
        var path = document.Path;
        if (string.IsNullOrWhiteSpace(path))
            path = await (RequestSavePathAsync?.Invoke("untitled.txt") ?? Task.FromResult<string?>(null));
        if (!string.IsNullOrWhiteSpace(path)) await SaveToPathAsync(document, path);
    }

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        if (IsSaving) return;
        if (ActiveDocument is null) NewDocument();
        var document = ActiveDocument;
        if (document is null) return;
        if (_files is null) { StatusText = LocalizedText.Ref("code_editor.status.connect_before_save"); return; }
        var suggestedName = string.IsNullOrWhiteSpace(document.Path) ? "untitled.txt" : Path.GetFileName(document.Path) ?? "untitled.txt";
        var path = await (RequestSavePathAsync?.Invoke(suggestedName) ?? Task.FromResult<string?>(null));
        if (!string.IsNullOrWhiteSpace(path)) await SaveToPathAsync(document, path);
    }

    [RelayCommand]
    private async Task ChooseEncodingAsync()
    {
        if (IsSaving || string.IsNullOrWhiteSpace(CurrentPath)) return;
        var action = await (RequestEncodingActionAsync?.Invoke() ?? Task.FromResult<EncodingDialogAction?>(null));
        if (action is null) return;
        var encoding = await (RequestEncodingAsync?.Invoke() ?? Task.FromResult<string?>(null));
        if (string.IsNullOrWhiteSpace(encoding)) return;
        if (action == EncodingDialogAction.Reopen)
            await ReopenWithEncodingAsync(encoding);
        else
            await SaveWithEncodingAsync(encoding);
    }

    private async Task ReopenWithEncodingAsync(string encodingName)
    {
        if (IsSaving) return;
        if (string.IsNullOrWhiteSpace(CurrentPath) || !TextFileEncodings.IsSupported(encodingName)) return;
        if (IsDirty && ActiveDocument is not null && !(await (RequestDiscardChangesAsync?.Invoke(ActiveDocument) ?? Task.FromResult(false)))) return;
        EncodingName = encodingName;
        await OpenPathAsync(CurrentPath, forceReload: true, allowDiscardDirty: true);
    }

    private async Task SaveWithEncodingAsync(string encodingName)
    {
        if (IsSaving) return;
        var document = ActiveDocument;
        if (document is null || string.IsNullOrWhiteSpace(document.Path) || !TextFileEncodings.IsSupported(encodingName)) return;
        // Assigning the view-model encoding propagates to the active document.
        EncodingName = encodingName;
        await SaveToPathAsync(document, document.Path!);
    }

    [RelayCommand]
    private async Task OpenSettingsAsync() => await (RequestSettingsAsync?.Invoke() ?? Task.CompletedTask);

    [RelayCommand]
    private void CloseSettings() => CloseSettingsAction?.Invoke();

    public async Task AddWorkspaceRootAsync(string path)
    {
        if (_files is null) { StatusText = LocalizedText.Ref("code_editor.status.connect_before_open"); return; }
        if (WorkspaceRoots.Any(root => PathEquals(root.Path, path)))
        {
            StatusText = LocalizedText.Ref("code_editor.status.folder_already_open", path);
            return;
        }
        var root = CreateFolderNode(FolderName(path), path, true);
        WorkspaceRoots.Add(root);
        await LoadFolderAsync(root, force: true);
        root.IsExpanded = true;
        SelectedFolderNode = root;
    }

    public async Task OpenPathAsync(string path, bool forceReload = false, bool allowDiscardDirty = false)
    {
        if (_files is null) { StatusText = LocalizedText.Ref("code_editor.status.connect_before_open"); return; }
        var existing = OpenDocuments.FirstOrDefault(document => !string.IsNullOrWhiteSpace(document.Path) && PathEquals(document.Path, path));
        if (existing is not null && !forceReload)
        {
            ActivateDocument(existing);
            return;
        }
        if (existing is not null && existing.IsDirty && !allowDiscardDirty)
        {
            StatusText = LocalizedText.Ref("code_editor.status.save_or_discard");
            return;
        }
        // Reserve the next read revision: a newer open or a new tab wins over this late result.
        var revision = ++_openRevision;
        try
        {
            var bytes = await _files.ReadFileAsync(path);
            if (revision != _openRevision) return;
            // A missing file leaves every open tab untouched instead of clearing the editor.
            if (bytes is null) { StatusText = LocalizedText.Ref("code_editor.status.file_missing"); return; }
            var encoding = existing?.EncodingName ?? DefaultEncodingName;
            var text = TextFileEncodings.Decode(bytes, encoding);
            if (existing is null)
            {
                existing = new CodeEditorDocument(path, text, encoding,
                    LocalizedText.Format("code_editor.document.untitled_number", ++_untitledSequence));
                OpenDocuments.Add(existing);
            }
            else
            {
                existing.Text = text;
                existing.EncodingName = encoding;
                existing.IsDirty = false;
            }
            ActivateDocument(existing);
            StatusText = LocalizedText.Ref("code_editor.status.opened", Path.GetFileName(path), encoding);
        }
        catch (Exception ex)
        {
            if (revision != _openRevision) return;
            StatusText = LocalizedText.Ref("code_editor.status.open_failed", ex.Message);
        }
    }

    private async Task LoadFolderAsync(CodeEditorFolderNode node, bool force)
    {
        if (_files is null || node.IsLoading || (node.IsLoaded && !force)) return;
        node.IsLoading = true;
        try
        {
            var directory = await _files.GetDirectoryAsync(node.Path);
            node.Children.Clear();
            foreach (var child in directory.Directories)
                node.Children.Add(CreateFolderNode(child.Name, child.Path, true));
            foreach (var file in directory.Files)
                node.Children.Add(new CodeEditorFolderNode(file.Name, file.Path, false));
            node.IsLoaded = true;
            StatusText = LocalizedText.Ref("code_editor.status.folder_loaded", directory.Name);
        }
        catch (Exception ex)
        {
            StatusText = LocalizedText.Ref("code_editor.status.folder_load_failed", node.Path, ex.Message);
        }
        finally { node.IsLoading = false; }
    }

    private CodeEditorFolderNode CreateFolderNode(string name, string path, bool isRoot)
    {
        var node = new CodeEditorFolderNode(string.IsNullOrWhiteSpace(name) ? path : name, path, true, isRoot);
        node.ExpandRequested = expanded => LoadFolderAsync(expanded, force: false);
        return node;
    }

    private async Task SaveToPathAsync(CodeEditorDocument document, string path)
    {
        if (_files is null) { StatusText = LocalizedText.Ref("code_editor.status.connect_before_save"); return; }
        if (IsSaving) return;
        // Snapshot the revision, bytes and encoding so the write describes exactly the tab the
        // user asked to save.
        var revision = document.Revision;
        var content = TextFileEncodings.Encode(document.Text, document.EncodingName);
        var encoding = document.EncodingName;
        IsSaving = true;
        try
        {
            await _files.WriteFileAsync(path, content);
            // An edit made while the write was in flight keeps its dirty flag.
            if (document.Revision == revision)
            {
                document.Path = path;
                document.EncodingName = encoding;
                document.IsDirty = false;
            }
            if (ReferenceEquals(ActiveDocument, document))
            {
                CurrentPath = document.Path;
                IsDirty = document.IsDirty;
            }
            OnPropertyChanged(nameof(DocumentName));
            StatusText = LocalizedText.Ref("code_editor.status.saved", Path.GetFileName(path), encoding);
        }
        catch (Exception ex)
        {
            // The write is unconfirmed. Leave the tab dirty so the user verifies before retrying.
            StatusText = LocalizedText.Ref("code_editor.status.save_failed", ex.Message);
        }
        finally { IsSaving = false; }
    }

    private static bool IsAncestor(CodeEditorFolderNode root, CodeEditorFolderNode node)
        => ReferenceEquals(root, node) || root.Children.Any(child => IsAncestor(child, node));

    private void ActivateDocument(CodeEditorDocument document)
    {
        if (!ReferenceEquals(ActiveDocument, document))
        {
            ActiveDocument = document;
            return;
        }
        _isLoadingDocument = true;
        Text = document.Text;
        CurrentPath = document.Path;
        EncodingName = document.EncodingName;
        IsDirty = document.IsDirty;
        _isLoadingDocument = false;
        OnPropertyChanged(nameof(DocumentName));
    }

    private bool PathEquals(string left, string right) => _pathComparer.Equals(left, right);

    private static string FolderName(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        if (string.IsNullOrEmpty(trimmed)) return path;
        var separator = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        return separator >= 0 && separator < trimmed.Length - 1 ? trimmed[(separator + 1)..] : trimmed;
    }

}
