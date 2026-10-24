using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Apps.CodeEditor;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git;

public enum EditorDraftAction { Cancel, Save, Discard }

/// <summary>Window-owned coordinator. File buffers survive tool-window navigation and Git refreshes.</summary>
public sealed partial class ProjectEditorViewModel : ObservableObject, IDisposable
{
    private readonly IRemoteGitClient _client;
    private bool _disposed;
    private bool _preparing;
    private bool _refreshing;
    private bool _refreshRequested;
    private int _diffRequest;
    private bool _presentingDiff;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, (GitFileChangeDto File, string? Commit)> _diffs = [];

    public ProjectEditorViewModel(GitClientViewModel git, IRemoteGitClient client, IExplorerClient? files,
        bool pathCaseSensitive, string defaultEncoding = "UTF-8")
    {
        Git = git;
        _client = client;
        Git.RepositoryPathComparison = pathCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        Editor = new CodeEditorViewModel(files, pathCaseSensitive, defaultEncoding, files as IEditorFileWriter)
        { OpenFilesOnSelection = false, IsProjectWorkspace = true };
        Git.OpenEditorProjectAsync = OpenRepositoryAsync;
        Git.OpenPlainFolderAsync = OpenPlainFolderAsync;
        Git.PrepareEditorOperationAsync = PrepareOperationAsync;
        Git.OpenEditorFileAsync = OpenWorkingTreeFileAsync;
        Git.OpenEditorDiffAsync = OpenDiffAsync;
        Git.PropertyChanged += GitChanged;
        Editor.FileSaved += FileSaved;
        Editor.PropertyChanged += EditorChanged;
        Git.RefreshEditorLocalization();
    }

    public GitClientViewModel Git { get; }
    public CodeEditorViewModel Editor { get; }
    public Func<IReadOnlyList<CodeEditorDocument>, Task<EditorDraftAction>>? RequestDraftActionAsync { get; set; }
    [ObservableProperty] private string? _projectPath;
    [ObservableProperty] private string? _projectName;
    [ObservableProperty] private bool _isCommitVisible;
    [ObservableProperty] private bool _isGitVisible;
    public bool CanClose => !Git.IsBusy && !Editor.IsSaving && !_preparing && !_refreshing;

    partial void OnIsCommitVisibleChanged(bool value) => Editor.IsSidebarVisible = !value;

    [RelayCommand]
    private void ToggleProject()
    {
        if (IsCommitVisible) { IsCommitVisible = false; Editor.IsSidebarVisible = true; }
        else Editor.IsSidebarVisible = !Editor.IsSidebarVisible;
    }

    [RelayCommand]
    private void ToggleCommit() => IsCommitVisible = !IsCommitVisible;

    [RelayCommand]
    private async Task CloseProjectAsync()
    {
        if (!CanClose || !await PrepareOperationAsync()) return;
        Git.Stop();
        Editor.ResetWorkspace();
        _diffs.Clear();
        _diffRequest++;
        IsCommitVisible = false;
        IsGitVisible = false;
        ProjectPath = null;
        ProjectName = null;
        Git.SelectedRepository = null;
        Git.IsPickerMode = true;
    }

    [RelayCommand]
    private async Task InitializeGitAsync()
    {
        if (ProjectPath is not null && Git.SelectedRepository is null && !Git.IsBusy)
            await Git.InitializeEditorProjectAsync(ProjectPath);
    }

    [RelayCommand]
    private void ShowGit(GitClientPage page)
    {
        IsGitVisible = !IsGitVisible || Git.ActivePage != page;
        Git.ActivePage = page;
    }

    private async Task<bool> OpenRepositoryAsync(GitRepositoryDto repository)
    {
        if (!await PrepareOperationAsync()) return false;
        return await OpenFolderCoreAsync(repository.Path, repository.Name);
    }

    private async Task OpenPlainFolderAsync(string path)
    {
        if (!await PrepareOperationAsync()) return;
        var name = path.Replace('\\', '/').TrimEnd('/').Split('/').Last();
        if (!await OpenFolderCoreAsync(path, string.IsNullOrEmpty(name) ? path : name)) return;
        Git.Stop();
        Git.SelectedRepository = null;
        Git.IsPickerMode = false;
        IsGitVisible = false;
    }

    private async Task<bool> OpenFolderCoreAsync(string path, string name)
    {
        if (_disposed || Editor.HasUnsavedDocuments || Editor.IsSaving) return false;
        _diffRequest++;
        _diffs.Clear();
        Editor.ResetWorkspace();
        ProjectPath = path;
        ProjectName = name;
        IsCommitVisible = false;
        Editor.IsSidebarVisible = true;
        await Editor.AddWorkspaceRootAsync(path);
        return !_disposed;
    }

    /// <summary>Used by every command that changes the worktree/index, including conflict operations.</summary>
    public async Task<bool> PrepareOperationAsync()
    {
        if (_disposed || _preparing || _refreshing || Editor.IsSaving) return false;
        if (!Editor.HasUnsavedDocuments) return true;
        _preparing = true;
        try
        {
            var documents = Editor.OpenDocuments.Where(item => item.IsDirty).ToArray();
            var action = await (RequestDraftActionAsync?.Invoke(documents) ?? Task.FromResult(EditorDraftAction.Cancel));
            if (action == EditorDraftAction.Cancel || _disposed) return false;
            foreach (var document in documents)
            {
                if (action == EditorDraftAction.Save)
                {
                    if (!await Editor.SaveDocumentAsync(document)) return false;
                }
                else if (!await Editor.DiscardDocumentAsync(document)) return false;
            }
            return !Editor.HasUnsavedDocuments;
        }
        finally { _preparing = false; }
    }

    public async Task OpenWorkingTreeFileAsync(GitFileChangeDto file)
    {
        if (ProjectPath is null || _disposed) return;
        var relative = file.Path.Replace('\\', '/');
        // Git supplies relative paths. Do not let a malformed entry target a different host path.
        if (relative.StartsWith('/') || relative.Contains(':') || relative.Split('/').Any(part => part is "." or "..")) return;
        var separator = ProjectPath.Contains('\\') ? '\\' : '/';
        var path = ProjectPath.TrimEnd('/', '\\') + separator + relative.Replace('/', separator);
        await Editor.OpenPathAsync(path);
    }

    public async Task OpenDiffAsync(GitFileChangeDto file, string? commit)
    {
        var repository = Git.SelectedRepository;
        if (repository is null || _disposed) return;
        var request = ++_diffRequest;
        try
        {
            var diff = await _client.GetDiffAsync(repository.Id, file.Path, file.Staged, commit, _lifetime.Token);
            if (_disposed || request != _diffRequest || Git.SelectedRepository?.Id != repository.Id) return;
            var key = $"{repository.Id}:{commit ?? (file.Staged ? "index" : "worktree")}:{file.Path}";
            _diffs[key] = (file, commit);
            PresentDiff(key, file, commit, diff);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) { Editor.StatusText = LocalizedText.Ref("git.vm.diff_failed_format", ex.Message); }
    }

    private void PresentDiff(string key, GitFileChangeDto file, string? commit, GitDiffDto diff)
    {
        _presentingDiff = true;
        try
        {
            Editor.OpenComparison(key, DiffTitle(file, commit), diff.Patch,
                diff.Binary ? "git.dialog.diff.binary_message" : diff.Truncated ? "git.dialog.diff.truncated_message"
                : string.IsNullOrEmpty(diff.Patch) ? "git.dialog.diff.empty_message" : null);
        }
        finally { _presentingDiff = false; }
    }

    private void EditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || _presentingDiff || e.PropertyName != nameof(CodeEditorViewModel.ActiveDocument)) return;
        _diffRequest++;
        if (Editor.ActiveDocument?.ComparisonKey is { } key && _diffs.TryGetValue(key, out var source))
            _ = OpenDiffAsync(source.File, source.Commit);
    }

    private static string DiffTitle(GitFileChangeDto file, string? commit)
    {
        var label = commit is not null ? commit[..Math.Min(8, commit.Length)]
            : LocalizedText.Get(file.Staged ? "git.editor.index" : "git.editor.worktree");
        return $"{file.Path} · {label}";
    }

    public void RefreshLocalization()
    {
        foreach (var document in Editor.OpenDocuments)
            if (document.ComparisonKey is { } key && _diffs.TryGetValue(key, out var source))
                document.TabTitle = DiffTitle(source.File, source.Commit);
        Editor.RefreshLocalization();
        Git.RefreshEditorLocalization();
    }

    private void GitChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GitClientViewModel.IsBusy)) return;
        Editor.IsOperationLocked = Git.IsBusy;
        if (!Git.IsBusy && !_preparing) _ = RefreshEditorAsync();
    }

    private async void FileSaved(object? sender, string path)
    {
        if (_preparing || _disposed || Git.IsBusy || Git.SelectedRepository is null) return;
        var active = Editor.ActiveDocument;
        try
        {
            await Git.RefreshCommand.ExecuteAsync(null);
            if (ReferenceEquals(Editor.ActiveDocument, active) && active?.ComparisonKey is { } key && _diffs.TryGetValue(key, out var source))
                await OpenDiffAsync(source.File, source.Commit);
        }
        catch (Exception ex) { Editor.StatusText = LocalizedText.Ref("git.vm.diff_failed_format", ex.Message); }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_disposed || Git.IsBusy || Editor.IsSaving) return;
        if (Git.SelectedRepository is not null) await Git.RefreshCommand.ExecuteAsync(null);
        await RefreshEditorAsync();
    }

    private async Task RefreshEditorAsync()
    {
        if (_disposed || Git.IsBusy || Editor.IsSaving || ProjectPath is null) return;
        if (_refreshing) { _refreshRequested = true; return; }
        _refreshing = true;
        Editor.IsOperationLocked = true;
        try
        {
            do
            {
                _refreshRequested = false;
                await Editor.RefreshOpenDocumentsAsync();
                foreach (var root in Editor.WorkspaceRoots.ToArray()) await Editor.RefreshFolderCommand.ExecuteAsync(root);
                var active = Editor.ActiveDocument;
                if (active?.ComparisonKey is { } key && _diffs.TryGetValue(key, out var source))
                    await OpenDiffAsync(source.File, source.Commit);
            } while (_refreshRequested && !_disposed && !Git.IsBusy);
        }
        finally { _refreshing = false; Editor.IsOperationLocked = Git.IsBusy; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _diffRequest++;
        _lifetime.Cancel();
        Git.Stop();
        Git.PropertyChanged -= GitChanged;
        Editor.FileSaved -= FileSaved;
        Editor.PropertyChanged -= EditorChanged;
        Editor.Dispose();
    }
}
