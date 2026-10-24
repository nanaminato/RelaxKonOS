using System.Collections.ObjectModel;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.TextEditor;
using RelaxKonOS.Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Apps.ImageViewer;
using Avalonia.Media;
using RelaxKonOS.Client.Controls.Diff;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.CodeEditor;

/// <summary>Owns the window-local multi-root workspace and the currently open editor documents.</summary>
public sealed partial class CodeEditorViewModel : ObservableObject, IDisposable
{
    private readonly IExplorerClient? _files;
    private readonly StringComparer _pathComparer;
    private bool _isLoadingDocument;
    private int _untitledSequence;

    // Bumped for every read that will replace or add a document. A late read whose revision no
    // longer matches must not activate its file over whatever the user opened meanwhile.
    private int _openRevision;
    private bool _disposed;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IEditorFileWriter? _conditionalWriter;

    public CodeEditorViewModel(IExplorerClient? files, bool pathCaseSensitive = true, string defaultEncodingName = "UTF-8",
        IEditorFileWriter? conditionalWriter = null)
    {
        _files = files;
        _conditionalWriter = conditionalWriter;
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
    [ObservableProperty] private bool _isSidebarVisible = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditDocument))]
    private bool _isSaving;
    [ObservableProperty] private bool _showImageSource;
    [ObservableProperty] private bool _imageAtActualSize;
    [ObservableProperty] private string _caretPosition = "1:1";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProjectEmptyState))]
    private bool _isProjectWorkspace;
    public bool ShowProjectEmptyState => IsProjectWorkspace && ActiveDocument is null;
    public bool HasDocuments => OpenDocuments.Count > 0;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditDocument))]
    private bool _isOperationLocked;

    /// <summary>False while a write is in flight; the editor and its command surface bind to this.</summary>
    public bool CanEditDocument => !IsSaving && !IsOperationLocked && !IsTreeBusy;
    public bool IsReadOnly => !CanEditDocument || ActiveDocument?.IsReadOnly == true;
    public bool HasImageSourceText => ActiveDocument?.ContentKind == CodeEditorContentKind.Image
        && string.Equals(Path.GetExtension(CurrentPath), ".svg", StringComparison.OrdinalIgnoreCase);
    public bool IsTextDocument => ActiveDocument?.ContentKind == CodeEditorContentKind.Text || (HasImageSourceText && ShowImageSource);
    public bool IsImageDocument => ActiveDocument?.ContentKind == CodeEditorContentKind.Image && !ShowImageSource;
    public bool IsBinaryDocument => ActiveDocument?.ContentKind == CodeEditorContentKind.Binary;
    public bool CanSaveDocument => CanEditDocument && ActiveDocument?.IsReadOnly != true && (ActiveDocument is null || IsTextDocument);
    public bool IsComparisonDocument => ActiveDocument?.ContentKind == CodeEditorContentKind.Comparison;
    public DiffDocument? Comparison => ActiveDocument?.Comparison;
    public string ComparisonNotice => ActiveDocument?.ComparisonNoticeKey is { } key ? LocalizedText.Get(key) : string.Empty;
    public bool HasComparisonNotice => !string.IsNullOrEmpty(ActiveDocument?.ComparisonNoticeKey);
    public bool HasExternalChanges => ActiveDocument?.HasExternalChanges == true;
    public bool IsTextReadOnly => IsTextDocument && ActiveDocument?.IsReadOnly == true;
    public string NewlineText => TextFileCodec.Newline(Text).ToUpperInvariant();
    public event EventHandler<string>? FileSaved;
    public bool OpenFilesOnSelection { get; set; } = true;
    public IImage? PreviewImage => ActiveDocument?.ImageSource;
    public double PreviewWidth => ImageAtActualSize ? PreviewImage?.Size.Width ?? double.NaN : double.NaN;
    public double PreviewHeight => ImageAtActualSize ? PreviewImage?.Size.Height ?? double.NaN : double.NaN;
    public string FileSizeText => LocalizedText.Format("code_editor.preview.bytes", ActiveDocument?.ByteLength ?? 0);
    public string ImageDimensionsText => PreviewImage is { } image
        ? LocalizedText.Format("image_viewer.dimensions", (int)Math.Ceiling(image.Size.Width), (int)Math.Ceiling(image.Size.Height)) : string.Empty;

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
    public bool HasOpenFile => !string.IsNullOrWhiteSpace(CurrentPath);

    public Func<Task<string?>>? RequestFileAsync { get; set; }
    public Func<Task<string?>>? RequestFolderAsync { get; set; }
    public Func<string, Task<string?>>? RequestSavePathAsync { get; set; }
    public Func<CodeEditorDocument, Task<bool>>? RequestDiscardChangesAsync { get; set; }
    public Func<CodeEditorDocument, Task<bool>>? RequestCloseDocumentAsync { get; set; }
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
        OnPropertyChanged(nameof(NewlineText));
        if (ActiveDocument is null || !IsTextDocument || ActiveDocument.IsReadOnly) return;
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
        OnPropertyChanged(nameof(ShowProjectEmptyState));
        _isLoadingDocument = true;
        ShowImageSource = value?.ShowSource ?? false;
        Text = value?.Text ?? string.Empty;
        CurrentPath = value?.Path;
        EncodingName = value?.EncodingName ?? "UTF-8";
        IsDirty = value?.IsDirty ?? false;
        _isLoadingDocument = false;
        OnPropertyChanged(nameof(DocumentName));
        ImageAtActualSize = false;
        NotifyContentChanged();
    }

    partial void OnIsSavingChanged(bool value) => NotifySaveCommands();
    partial void OnIsOperationLockedChanged(bool value) => NotifySaveCommands();
    partial void OnShowImageSourceChanged(bool value)
    {
        if (!_isLoadingDocument && ActiveDocument is { } document) document.ShowSource = value;
        NotifyContentChanged();
    }
    partial void OnImageAtActualSizeChanged(bool value)
    {
        OnPropertyChanged(nameof(PreviewWidth));
        OnPropertyChanged(nameof(PreviewHeight));
    }

    private void NotifySaveCommands()
    {
        OnPropertyChanged(nameof(CanSaveDocument));
        OnPropertyChanged(nameof(IsReadOnly));
        SaveCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        ChooseEncodingCommand.NotifyCanExecuteChanged();
    }

    private void NotifyContentChanged()
    {
        foreach (var name in new[] { nameof(IsTextDocument), nameof(IsImageDocument), nameof(IsBinaryDocument),
            nameof(HasImageSourceText), nameof(PreviewImage), nameof(PreviewWidth), nameof(PreviewHeight),
            nameof(FileSizeText), nameof(ImageDimensionsText), nameof(IsComparisonDocument), nameof(Comparison),
            nameof(ComparisonNotice), nameof(HasComparisonNotice), nameof(HasExternalChanges), nameof(IsReadOnly), nameof(IsTextReadOnly) }) OnPropertyChanged(name);
        NotifySaveCommands();
        OnPropertyChanged(nameof(HasDocuments));
    }

    public void RefreshLocalization()
    {
        NotifyContentChanged();
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DocumentName));
        OnPropertyChanged(nameof(LineCountText));
        OnPropertyChanged(nameof(CharacterCountText));
    }

    [RelayCommand]
    private void ToggleImageSource()
    {
        if (!HasImageSourceText || ActiveDocument is not { } document) return;
        if (ShowImageSource)
        {
            try
            {
                var image = ImageViewerDecoder.Load(document.Path!, TextFileEncodings.Encode(document.Text, document.EncodingName));
                (document.ImageSource as IDisposable)?.Dispose();
                document.ImageSource = image;
            }
            catch (Exception ex)
            {
                StatusText = LocalizedText.Ref("code_editor.status.open_failed", ex.Message);
                return;
            }
        }
        ShowImageSource = !ShowImageSource;
    }

    partial void OnSelectedFolderNodeChanged(CodeEditorFolderNode? value)
    {
        if (value is null || value.IsPlaceholder) return;
        if (value.IsDirectory)
        {
            value.IsExpanded = true;
            return;
        }
        if (OpenFilesOnSelection) _ = OpenPathAsync(value.Path);
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    /// <summary>Publishes generated text without replacing existing buffers, including while its producing Git operation finishes.</summary>
    public void OpenGeneratedTextDocument(string title, string text)
    {
        if (_disposed) return;
        _openRevision++;
        var document = new CodeEditorDocument(null, text, DefaultEncodingName, title) { IsDirty = true };
        OpenDocuments.Add(document);
        ActiveDocument = document;
    }

    [RelayCommand]
    private void NewDocument()
    {
        if (!CanEditDocument || _disposed) return;
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
        if (!CanEditDocument) return;
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
        if (!CanEditDocument) return;
        document ??= ActiveDocument;
        if (document is null) return;
        if (document.IsDirty && !(await ((RequestCloseDocumentAsync ?? RequestDiscardChangesAsync)?.Invoke(document) ?? Task.FromResult(false)))) return;

        var index = OpenDocuments.IndexOf(document);
        // Removing the selected ListBox item synchronously writes null through its two-way
        // binding. Capture identity first so that notification cannot suppress selecting a neighbor.
        var wasActive = ReferenceEquals(ActiveDocument, document);
        OpenDocuments.Remove(document);
        document.Dispose();
        NotifyContentChanged();
        if (!wasActive) return;
        ActiveDocument = OpenDocuments.Count == 0 ? null : OpenDocuments[Math.Clamp(index, 0, OpenDocuments.Count - 1)];
    }

    [RelayCommand(CanExecute = nameof(CanSaveDocument))]
    private async Task SaveAsync()
    {
        if (!CanSaveDocument) return;
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

    [RelayCommand(CanExecute = nameof(CanSaveDocument))]
    private async Task SaveAsAsync()
    {
        if (!CanSaveDocument) return;
        if (ActiveDocument is null) NewDocument();
        var document = ActiveDocument;
        if (document is null) return;
        if (_files is null) { StatusText = LocalizedText.Ref("code_editor.status.connect_before_save"); return; }
        var suggestedName = string.IsNullOrWhiteSpace(document.Path) ? "untitled.txt" : Path.GetFileName(document.Path) ?? "untitled.txt";
        var path = await (RequestSavePathAsync?.Invoke(suggestedName) ?? Task.FromResult<string?>(null));
        if (!string.IsNullOrWhiteSpace(path)) await SaveToPathAsync(document, path);
    }

    [RelayCommand(CanExecute = nameof(CanSaveDocument))]
    private async Task ChooseEncodingAsync()
    {
        if (!CanSaveDocument || string.IsNullOrWhiteSpace(CurrentPath)) return;
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
            _openRevision++;
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
        var existingRevision = existing?.Revision;
        try
        {
            var bytes = await _files.ReadFileAsync(path, _lifetime.Token);
            if (_disposed || revision != _openRevision) return;
            if (existing is not null && existing.Revision != existingRevision)
            {
                StatusText = LocalizedText.Ref("code_editor.status.save_or_discard");
                return;
            }
            // A missing file leaves every open tab untouched instead of clearing the editor.
            if (bytes is null) { StatusText = LocalizedText.Ref("code_editor.status.file_missing"); return; }
            var encoding = existing?.EncodingName ?? CodeEditorFileContent.DetectEncoding(bytes, DefaultEncodingName);
            IImage? image = null;
            var text = string.Empty;
            var contentKind = CodeEditorContentKind.Text;
            if (CodeEditorFileContent.IsImage(path))
            {
                var svg = string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase);
                try
                {
                    image = ImageViewerDecoder.Load(path, bytes);
                    contentKind = CodeEditorContentKind.Image;
                }
                catch
                {
                    // Invalid SVG remains editable; unreadable raster files get the binary card.
                    contentKind = svg ? CodeEditorContentKind.Text : CodeEditorContentKind.Binary;
                }
                if (svg) text = TextFileEncodings.Decode(bytes, encoding);
            }
            else
            {
                text = TextFileEncodings.Decode(bytes, encoding);
                if (CodeEditorFileContent.IsBinary(path, text))
                {
                    contentKind = CodeEditorContentKind.Binary;
                    text = string.Empty;
                }
            }
            if (existing is null)
            {
                existing = new CodeEditorDocument(path, text, encoding,
                    LocalizedText.Format("code_editor.document.untitled_number", ++_untitledSequence));
                OpenDocuments.Add(existing);
            }
            else
            {
                (existing.ImageSource as IDisposable)?.Dispose();
                existing.Text = text;
                existing.EncodingName = encoding;
                existing.IsDirty = false;
            }
            existing.ContentKind = contentKind;
            existing.ImageSource = image;
            existing.ByteLength = bytes.LongLength;
            existing.Version = GitTextFileWrite.Version(bytes);
            existing.HasExternalChanges = false;
            existing.IsReadOnly = _conditionalWriter is not null && bytes.Length > TextFileCodec.MaximumBytes;
            ActivateDocument(existing);
            StatusText = contentKind == CodeEditorContentKind.Text
                ? LocalizedText.Ref("code_editor.status.opened", Path.GetFileName(path), encoding)
                : LocalizedText.Ref("code_editor.preview.opened", Path.GetFileName(path));
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
            var directory = await _files.GetDirectoryAsync(node.Path, _lifetime.Token);
            if (_disposed) return;
            node.Children.Clear();
            foreach (var child in directory.Directories)
                node.Children.Add(CreateFolderNode(child.Name, child.Path, false));
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
        if (!CanEditDocument || document.IsReadOnly || (document.ContentKind != CodeEditorContentKind.Text
            && !string.Equals(Path.GetExtension(document.Path), ".svg", StringComparison.OrdinalIgnoreCase))) return;
        // Snapshot the revision, bytes and encoding so the write describes exactly the tab the
        // user asked to save.
        var revision = document.Revision;
        var content = TextFileEncodings.Encode(document.Text, document.EncodingName);
        var encoding = document.EncodingName;
        var saved = false;
        IsSaving = true;
        try
        {
            if (_conditionalWriter is not null)
                document.Version = await _conditionalWriter.WriteEditorFileAsync(path, content,
                    document.Path is not null && PathEquals(document.Path, path) ? document.Version : null, _lifetime.Token);
            else
                await _files.WriteFileAsync(path, content, _lifetime.Token);
            if (_disposed) return;
            document.HasExternalChanges = false;
            // An edit made while the write was in flight keeps its dirty flag.
            if (document.Revision == revision)
            {
                document.Path = path;
                document.EncodingName = encoding;
                document.IsDirty = false;
                document.ByteLength = content.LongLength;
                if (document.ContentKind == CodeEditorContentKind.Image
                    && !string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase))
                {
                    document.ContentKind = CodeEditorContentKind.Text;
                    (document.ImageSource as IDisposable)?.Dispose();
                    document.ImageSource = null;
                }
            }
            if (ReferenceEquals(ActiveDocument, document))
            {
                CurrentPath = document.Path;
                IsDirty = document.IsDirty;
                NotifyContentChanged();
            }
            OnPropertyChanged(nameof(DocumentName));
            StatusText = LocalizedText.Ref("code_editor.status.saved", Path.GetFileName(path), encoding);
            saved = true;
        }
        catch (RelaxKonOSAuthException ex) when (ex.Type.EndsWith("/text-file-changed", StringComparison.Ordinal))
        {
            var samePath = document.Path is not null && PathEquals(document.Path, path);
            if (samePath) document.HasExternalChanges = true;
            StatusText = LocalizedText.Ref(samePath ? "git.editor.external_changes" : "git.editor.target_exists");
            NotifyContentChanged();
        }
        catch (Exception ex)
        {
            // The write is unconfirmed. Leave the tab dirty so the user verifies before retrying.
            StatusText = LocalizedText.Ref("code_editor.status.save_failed", ex.Message);
        }
        finally
        {
            IsSaving = false;
            if (saved) FileSaved?.Invoke(this, path);
        }
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
        ShowImageSource = document.ShowSource;
        Text = document.Text;
        CurrentPath = document.Path;
        EncodingName = document.EncodingName;
        IsDirty = document.IsDirty;
        _isLoadingDocument = false;
        OnPropertyChanged(nameof(DocumentName));
        NotifyContentChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _openRevision++;
        foreach (var document in OpenDocuments) document.Dispose();
    }

    public void OpenComparison(string key, string title, string? patch, string? noticeKey)
    {
        if (_disposed) return;
        _openRevision++;
        var document = OpenDocuments.FirstOrDefault(item => item.ComparisonKey == key);
        if (document is null)
        {
            document = new CodeEditorDocument(null, string.Empty, "UTF-8", title)
            {
                ComparisonKey = key, TabTitle = title, ContentKind = CodeEditorContentKind.Comparison, IsReadOnly = true,
            };
            OpenDocuments.Add(document);
        }
        document.Comparison = DiffDocument.Parse(patch);
        document.ComparisonNoticeKey = noticeKey;
        ActivateDocument(document);
    }

    [RelayCommand]
    public async Task SaveAllAsync()
    {
        if (!CanEditDocument) return;
        foreach (var document in OpenDocuments.Where(item => item.IsDirty).ToArray())
        {
            if (!await SaveDocumentAsync(document)) return;
        }
    }

    public async Task<bool> SaveDocumentAsync(CodeEditorDocument document)
    {
        if (!CanEditDocument || document.IsReadOnly || !OpenDocuments.Contains(document)) return false;
        var path = document.Path;
        if (string.IsNullOrWhiteSpace(path)) path = await (RequestSavePathAsync?.Invoke(document.DisplayName) ?? Task.FromResult<string?>(null));
        if (string.IsNullOrWhiteSpace(path)) return false;
        await SaveToPathAsync(document, path);
        return !document.IsDirty;
    }

    public async Task<bool> DiscardDocumentAsync(CodeEditorDocument document)
    {
        if (_disposed || IsSaving || !OpenDocuments.Contains(document)) return false;
        if (document.Path is null)
        {
            document.IsDirty = false;
            await CloseDocumentCommand.ExecuteAsync(document);
            return true;
        }
        if (_files is not null)
        {
            var revision = document.Revision;
            try
            {
                var bytes = await _files.ReadFileAsync(document.Path, _lifetime.Token);
                if (_disposed || document.Revision != revision || !OpenDocuments.Contains(document)) return false;
                if (bytes is null)
                {
                    document.IsDirty = false;
                    await CloseDocumentCommand.ExecuteAsync(document);
                    return true;
                }
            }
            catch (Exception ex)
            {
                StatusText = LocalizedText.Ref("code_editor.status.open_failed", ex.Message);
                return false;
            }
        }
        await OpenPathAsync(document.Path, forceReload: true, allowDiscardDirty: true);
        return !document.IsDirty;
    }

    public void ResetWorkspace()
    {
        if (HasUnsavedDocuments || IsSaving) throw new InvalidOperationException("Resolve drafts before resetting the editor.");
        _openRevision++;
        ActiveDocument = null;
        foreach (var document in OpenDocuments) document.Dispose();
        OpenDocuments.Clear();
        WorkspaceRoots.Clear();
        SelectedFolderNode = null;
        NotifyContentChanged();
    }

    /// <summary>Reload clean documents, preserving dirty buffers and marking external conflicts.</summary>
    public async Task RefreshOpenDocumentsAsync()
    {
        if (_files is null || _disposed || IsSaving) return;
        foreach (var document in OpenDocuments.Where(item => item.Path is not null).ToArray())
        {
            var revision = document.Revision;
            var path = document.Path!;
            try
            {
                var bytes = await _files.ReadFileAsync(path, _lifetime.Token);
                if (_disposed || !OpenDocuments.Contains(document) || document.Revision != revision || document.Path != path) continue;
                var version = bytes is null ? null : GitTextFileWrite.Version(bytes);
                if (version == document.Version) continue;
                document.HasExternalChanges = true;
                if (bytes is not null && !document.IsDirty && document.ContentKind == CodeEditorContentKind.Text)
                {
                    var text = TextFileEncodings.Decode(bytes, document.EncodingName);
                    if (CodeEditorFileContent.IsBinary(path, text)) document.IsReadOnly = true;
                    else
                    {
                        document.Text = text;
                        document.Version = version;
                        document.ByteLength = bytes.LongLength;
                        document.IsReadOnly = _conditionalWriter is not null && bytes.Length > TextFileCodec.MaximumBytes;
                        document.HasExternalChanges = false;
                    }
                }
                if (ReferenceEquals(document, ActiveDocument)) ActivateDocument(document);
            }
            catch (OperationCanceledException) when (_disposed) { return; }
            catch (Exception ex) { StatusText = LocalizedText.Ref("code_editor.status.open_failed", ex.Message); }
        }
    }

    [RelayCommand]
    private async Task ReloadDocumentAsync()
    {
        var document = ActiveDocument;
        if (!CanEditDocument || document?.Path is null) return;
        if (document.IsDirty && !(await (RequestDiscardChangesAsync?.Invoke(document) ?? Task.FromResult(false)))) return;
        await OpenPathAsync(document.Path, forceReload: true, allowDiscardDirty: true);
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
