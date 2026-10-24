using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.CodeEditor;

public partial class CodeEditorView
{
    private MenuItem ActionItem(string key, Func<Task> action, bool enabled = true)
    {
        var item = new MenuItem { Header = LocalizedText.Get("code_editor.context." + key), IsEnabled = enabled };
        item.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex)
            {
                if (_viewModel is { } vm) vm.StatusText = LocalizedText.Ref("code_editor.context.operation_failed", ex.Message);
            }
        };
        return item;
    }
    private async Task CopyPathAsync(string path)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(path);
    }
    private void OpenContext(Control owner, IEnumerable<Control> items, ContextRequestedEventArgs e)
    {
        owner.ContextMenu = new ContextMenu { ItemsSource = items };
        owner.ContextMenu.Open(owner);
        e.Handled = true;
    }
    private void DocumentTab_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: CodeEditorDocument doc } owner || _viewModel is not { } vm) return;
        var items = new List<Control>
        {
            ActionItem("save", () => vm.SaveDocumentAsync(doc), vm.CanEditDocument && !doc.IsReadOnly && doc.ContentKind == CodeEditorContentKind.Text),
            new Separator(),
            ActionItem("close", () => vm.CloseDocumentCommand.ExecuteAsync(doc), vm.CanEditDocument),
        };
        foreach (var (key, scope) in new[] { ("close_others", EditorCloseScope.Others), ("close_left", EditorCloseScope.Left),
            ("close_right", EditorCloseScope.Right), ("close_saved", EditorCloseScope.Saved), ("close_all", EditorCloseScope.All) })
            items.Add(ActionItem(key, () => vm.CloseDocumentsAsync(doc, scope), vm.CanEditDocument));
        if (doc.Path is { } path)
        {
            items.Add(new Separator());
            items.Add(ActionItem("copy_path", () => CopyPathAsync(path)));
            items.Add(ActionItem("copy_relative_path", () => CopyPathAsync(vm.RelativeWorkspacePath(path))));
        }
        OpenContext(owner, items, e);
    }
    private void ProjectNode_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: CodeEditorFolderNode node } owner || node.IsPlaceholder || _viewModel is not { } vm) return;
        var items = new List<Control>();
        if (!node.IsDirectory) items.Add(ActionItem("open", () => vm.OpenPathAsync(node.Path), vm.CanEditDocument));
        foreach (var (key, action) in new[] { ("newfile", EditorTreeAction.NewFile), ("newfolder", EditorTreeAction.NewFolder) })
            items.Add(ActionItem(key, () => vm.PerformTreeActionAsync(node, action),
                vm.CanEditDocument && (action != EditorTreeAction.NewFile || vm.CanCreateWorkspaceFile)));
        items.Add(new Separator());
        items.Add(ActionItem("copy_path", () => CopyPathAsync(node.Path)));
        items.Add(ActionItem("copy_relative_path", () => CopyPathAsync(vm.RelativeWorkspacePath(node.Path))));
        if (node.IsDirectory)
        {
            items.Add(ActionItem("refresh", () => vm.RefreshFolderCommand.ExecuteAsync(node)));
            items.Add(ActionItem("collapse", () => { node.IsExpanded = false; return Task.CompletedTask; }));
        }
        if (!node.IsWorkspaceRoot)
        {
            items.Add(new Separator());
            items.Add(ActionItem("rename", () => vm.PerformTreeActionAsync(node, EditorTreeAction.Rename), vm.CanEditDocument));
            items.Add(ActionItem("delete", () => vm.PerformTreeActionAsync(node, EditorTreeAction.Delete), vm.CanEditDocument));
        }
        else if (!vm.IsProjectWorkspace)
            items.Add(ActionItem("remove_root", () => { vm.RemoveFolderCommand.Execute(node); return Task.CompletedTask; }));
        OpenContext(owner, items, e);
    }
}
