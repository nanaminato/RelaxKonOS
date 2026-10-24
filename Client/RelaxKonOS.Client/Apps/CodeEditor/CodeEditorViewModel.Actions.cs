using RelaxKonOS.Client.Apps.Explorer;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.CodeEditor;

public enum EditorCloseScope { Others, Left, Right, Saved, All }
public enum EditorTreeAction { NewFile, NewFolder, Rename, Delete }

public sealed partial class CodeEditorViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditDocument))]
    private bool _isTreeBusy;
    partial void OnIsTreeBusyChanged(bool value) => NotifySaveCommands();
    private bool _treeOperation;
    public bool IsTreeOperationPending => _treeOperation;
    private bool _closingDocuments;
    public bool CanCreateWorkspaceFile => _conditionalWriter is not null || _files is IEditorFileWriter;
    public Func<string, string, Task<string?>>? RequestEntryNameAsync { get; set; }
    public Func<string, Task<bool>>? RequestDeleteEntryAsync { get; set; }

    public static bool IsValidEntryName(string? name) => !string.IsNullOrWhiteSpace(name)
        && name is not "." and not ".." && name.IndexOfAny(['/', '\\', '\0', ':']) < 0;

    public async Task CloseDocumentsAsync(CodeEditorDocument anchor, EditorCloseScope scope)
    {
        if (_closingDocuments || !CanEditDocument || !OpenDocuments.Contains(anchor)) return;
        _closingDocuments = true;
        try
        {
            var index = OpenDocuments.IndexOf(anchor);
            var targets = OpenDocuments.Where((document, i) => scope switch
            {
                EditorCloseScope.Others => document != anchor,
                EditorCloseScope.Left => i < index,
                EditorCloseScope.Right => i > index,
                EditorCloseScope.Saved => !document.IsDirty,
                _ => true,
            }).ToArray();
            foreach (var document in targets)
            {
                if (!CanEditDocument || _disposed) break;
                if (!OpenDocuments.Contains(document)) continue;
                await CloseDocumentAsync(document);
                // Cancellation or a failed save leaves this tab present. Stop the batch.
                if (OpenDocuments.Contains(document)) break;
            }
        }
        finally { _closingDocuments = false; }
    }

    private string NormalizeTreePath(string path) => path.Replace('\\', '/').TrimEnd('/');
    private bool AtOrBelow(string path, string parent) => _pathComparer.Equals(NormalizeTreePath(path), NormalizeTreePath(parent))
        || NormalizeTreePath(path).StartsWith(NormalizeTreePath(parent) + "/",
            _pathComparer == StringComparer.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private CodeEditorFolderNode? FindParent(CodeEditorFolderNode node)
    {
        CodeEditorFolderNode? Visit(CodeEditorFolderNode parent)
        {
            if (parent.Children.Contains(node)) return parent;
            foreach (var child in parent.Children.Where(item => item.IsDirectory))
                if (Visit(child) is { } found) return found;
            return null;
        }
        return WorkspaceRoots.Select(Visit).FirstOrDefault(parent => parent is not null);
    }
    public string RelativeWorkspacePath(string path)
    {
        var root = WorkspaceRoots.Where(item => AtOrBelow(path, item.Path)).OrderByDescending(item => item.Path.Length).FirstOrDefault();
        return root is null ? path : NormalizeTreePath(path)[NormalizeTreePath(root.Path).Length..].TrimStart('/');
    }

    public async Task PerformTreeActionAsync(CodeEditorFolderNode node, EditorTreeAction action)
    {
        if (_files is null || _disposed || !CanEditDocument || _treeOperation || node.IsPlaceholder) return;
        if (node.IsWorkspaceRoot && action is EditorTreeAction.Rename or EditorTreeAction.Delete) return;
        if (action == EditorTreeAction.NewFile && !CanCreateWorkspaceFile) return;
        var parent = FindParent(node);
        var folder = node.IsDirectory && action is EditorTreeAction.NewFile or EditorTreeAction.NewFolder ? node : parent;
        if (folder is null) return;
        _treeOperation = true;
        var changed = false;
        string? createdPath = null;
        try
        {
            string? name = null;
            if (action == EditorTreeAction.Delete)
            {
                if (!await (RequestDeleteEntryAsync?.Invoke(node.Path) ?? Task.FromResult(false))) return;
                foreach (var document in OpenDocuments.Where(doc => doc.Path is not null && AtOrBelow(doc.Path, node.Path) && doc.IsDirty).ToArray())
                {
                    if (!await ((RequestCloseDocumentAsync ?? RequestDiscardChangesAsync)?.Invoke(document) ?? Task.FromResult(false))) return;
                    changed |= !document.IsDirty;
                }
            }
            else
            {
                name = await (RequestEntryNameAsync?.Invoke(LocalizedText.Get("code_editor.context." + action.ToString().ToLowerInvariant()),
                    action == EditorTreeAction.Rename ? node.Name : string.Empty) ?? Task.FromResult<string?>(null));
                if (name is null) return;
                if (!IsValidEntryName(name)) { StatusText = LocalizedText.Ref("explorer.input_invalid"); return; }
            }
            if (_disposed || !CanEditDocument) return;
            // Prevent edits and saves while the remote path mutation is in flight.
            IsTreeBusy = true;
            _openRevision++;
            if (action == EditorTreeAction.Delete)
            {
                await _files.DeleteAsync(node.Path, _lifetime.Token);
                foreach (var doc in OpenDocuments.Where(doc => doc.Path is not null && AtOrBelow(doc.Path, node.Path)).ToArray())
                {
                    var wasActive = ActiveDocument == doc;
                    OpenDocuments.Remove(doc);
                    doc.Dispose();
                    if (wasActive) ActiveDocument = OpenDocuments.FirstOrDefault();
                }
            }
            else if (action == EditorTreeAction.Rename)
            {
                var entry = await _files.RenameAsync(node.Path, name!, _lifetime.Token);
                if (_disposed) return;
                foreach (var doc in OpenDocuments.Where(doc => doc.Path is not null && AtOrBelow(doc.Path, node.Path)).ToArray())
                    doc.Path = NormalizeTreePath(entry.Path) + NormalizeTreePath(doc.Path!)[NormalizeTreePath(node.Path).Length..];
                if (ActiveDocument is { } active) CurrentPath = active.Path;
            }
            else
            {
                var path = NormalizeTreePath(folder.Path) + "/" + name;
                if (action == EditorTreeAction.NewFolder) await _files.CreateDirectoryAsync(path, _lifetime.Token);
                else
                {
                    // Exclusive creation uses the same conditional writer as project saves.
                    var writer = _conditionalWriter ?? _files as IEditorFileWriter;
                    if (writer is null) throw new InvalidOperationException(LocalizedText.Get("code_editor.context.creation_unavailable"));
                    await writer.WriteEditorFileAsync(path, [], null, _lifetime.Token);
                    createdPath = path;
                }
            }
            if (_disposed) return;
            await LoadFolderAsync(folder, force: true);
            folder.IsExpanded = true;
            NotifyContentChanged();
            changed = true;
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) { StatusText = LocalizedText.Ref("code_editor.context.operation_failed", ex.Message); }
        finally
        {
            IsTreeBusy = false;
            _treeOperation = false;
            if (changed && !_disposed)
            {
                if (createdPath is not null) await OpenPathAsync(createdPath);
                FileSaved?.Invoke(this, folder.Path);
            }
        }
    }
}
