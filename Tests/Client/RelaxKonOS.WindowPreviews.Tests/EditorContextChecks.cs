using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.CodeEditor;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Localization;

internal static class EditorContextChecks
{
    private static void Check(bool result, string message)
    {
        if (!result) throw new Exception(message);
        Console.WriteLine("PASS CONTEXT: " + message);
    }
    public static async Task RunAsync()
    {
        using (var tabs = new CodeEditorViewModel(null))
        {
            CodeEditorDocument Add(string text = "")
            {
                tabs.NewDocumentCommand.Execute(null);
                if (text.Length > 0) tabs.Text = text;
                return tabs.ActiveDocument!;
            }
            var first = Add(); var anchor = Add(); var dirty = Add("draft"); var last = Add();
            tabs.RequestCloseDocumentAsync = _ => Task.FromResult(false);
            await tabs.CloseDocumentsAsync(anchor, EditorCloseScope.Right);
            Check(tabs.OpenDocuments.Contains(dirty) && tabs.OpenDocuments.Contains(last), "Canceling a dirty tab stops closing the remaining tabs.");
            tabs.RequestCloseDocumentAsync = tabs.SaveDocumentAsync;
            await tabs.CloseDocumentsAsync(anchor, EditorCloseScope.Right);
            Check(tabs.OpenDocuments.Contains(dirty) && tabs.OpenDocuments.Contains(last), "A failed draft save also stops a batch close.");
            await tabs.CloseDocumentsAsync(anchor, EditorCloseScope.Left);
            Check(!tabs.OpenDocuments.Contains(first) && tabs.OpenDocuments.Contains(anchor), "Close Left retains its anchor and right-hand tabs.");
            await tabs.CloseDocumentsAsync(anchor, EditorCloseScope.Saved);
            Check(tabs.OpenDocuments.SequenceEqual(new[] { dirty }) && dirty.Text == "draft", "Close Saved preserves dirty buffers.");
            var other = Add();
            tabs.RequestCloseDocumentAsync = _ => Task.FromResult(true);
            await tabs.CloseDocumentsAsync(other, EditorCloseScope.Others);
            Check(tabs.OpenDocuments.SequenceEqual(new[] { other }), "Close Others keeps the clicked tab.");
            await tabs.CloseDocumentsAsync(other, EditorCloseScope.All);
            Check(!tabs.HasDocuments && tabs.ActiveDocument is null, "Close All clears the active tab and tab strip.");
        }
        var files = DispatchProxy.Create<IExplorerClient, ProjectFiles>();
        var store = (ProjectFiles)(object)files;
        store.Files["/work/app.cs"] = Encoding.UTF8.GetBytes("initial");
        using var vm = new CodeEditorViewModel(files, conditionalWriter: store);
        await vm.AddWorkspaceRootAsync("/work");
        var root = vm.WorkspaceRoots.Single();
        vm.RequestEntryNameAsync = (_, _) => Task.FromResult<string?>("src");
        await vm.PerformTreeActionAsync(root, EditorTreeAction.NewFolder);
        Check(store.Directories.Contains("/work/src") && root.Children.Any(n => n.Name == "src"), "New Folder refreshes the tree.");
        var folder = root.Children.Single(n => n.Name == "src");
        vm.RequestEntryNameAsync = (_, _) => Task.FromResult<string?>("new.cs");
        await vm.PerformTreeActionAsync(folder, EditorTreeAction.NewFile);
        var draft = vm.ActiveDocument!;
        vm.Text = "unsaved";
        Check(store.Files.ContainsKey("/work/src/new.cs") && draft.Path == "/work/src/new.cs", "New File creates exclusively and opens its tab.");
        vm.RequestEntryNameAsync = (_, _) => Task.FromResult<string?>("new.cs");
        await vm.PerformTreeActionAsync(folder, EditorTreeAction.NewFile);
        Check(draft.Text == "unsaved" && store.Files["/work/src/new.cs"].Length == 0, "Creating an existing file cannot overwrite disk or its draft.");
        vm.RequestEntryNameAsync = (_, _) => Task.FromResult<string?>("../outside");
        await vm.PerformTreeActionAsync(folder, EditorTreeAction.NewFolder);
        Check(!store.Directories.Any(path => path.Contains("..")), "Names cannot traverse outside their selected folder.");
        vm.RequestEntryNameAsync = (_, _) => Task.FromResult<string?>("renamed");
        await vm.PerformTreeActionAsync(folder, EditorTreeAction.Rename);
        Check(draft.Path == "/work/renamed/new.cs" && vm.CurrentPath == draft.Path && draft.IsDirty && draft.Text == "unsaved",
            "Renaming a folder updates open descendant paths without losing drafts.");
        Check(vm.RelativeWorkspacePath(draft.Path!) == "renamed/new.cs" && vm.RelativeWorkspacePath("/work-other/a") == "/work-other/a",
            "Relative paths respect workspace boundaries.");
        var renamed = root.Children.Single(n => n.Name == "renamed");
        vm.RequestDeleteEntryAsync = _ => Task.FromResult(true);
        vm.RequestCloseDocumentAsync = _ => Task.FromResult(false);
        await vm.PerformTreeActionAsync(renamed, EditorTreeAction.Delete);
        Check(store.Files.ContainsKey(draft.Path!) && vm.OpenDocuments.Contains(draft), "Canceling the draft prompt prevents folder deletion.");
        vm.RequestCloseDocumentAsync = _ => Task.FromResult(true);
        store.FailMutation = true;
        await vm.PerformTreeActionAsync(renamed, EditorTreeAction.Delete);
        Check(vm.OpenDocuments.Contains(draft) && draft.IsDirty && vm.CanEditDocument, "Failed deletion retains open drafts and releases the edit lock.");
        store.FailMutation = false;
        await vm.PerformTreeActionAsync(renamed, EditorTreeAction.Delete);
        Check(!store.Files.ContainsKey("/work/renamed/new.cs") && !vm.OpenDocuments.Contains(draft), "Successful deletion removes descendant tabs and refreshes the tree.");
        await vm.PerformTreeActionAsync(root, EditorTreeAction.Delete);
        Check(store.Directories.Contains("/work"), "Workspace roots cannot be deleted through the tree menu.");

        await vm.OpenPathAsync("/work/app.cs");
        var background = vm.ActiveDocument!;
        vm.NewDocumentCommand.Execute(null);
        var active = vm.ActiveDocument;
        var view = new CodeEditorView { DataContext = vm };
        var host = new Window { Width = 960, Height = 640, Content = view };
        host.Show(); Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
        void RightClick(Control control)
        {
            var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), host)!.Value;
            host.MouseDown(point, MouseButton.Right); host.MouseUp(point, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
        }
        var tab = view.GetVisualDescendants().OfType<Grid>().Single(control => ReferenceEquals(control.DataContext, background)
            && control.ColumnDefinitions.Count == 4);
        RightClick(tab);
        Check(tab.ContextMenu is { IsOpen: true }, "Right-clicking an editor tab opens its actual context menu.");
        var close = tab.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, LocalizedText.Get("code_editor.context.close")));
        close.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Check(!vm.OpenDocuments.Contains(background) && vm.ActiveDocument == active, "The menu closes its clicked tab rather than the active tab.");
        tab.ContextMenu.Close();
        var row = view.GetVisualDescendants().OfType<StackPanel>().Single(control => control.Name == "ProjectNodeHeader" && ReferenceEquals(control.DataContext, root));
        RightClick(row);
        Check(row.ContextMenu is { IsOpen: true } && row.ContextMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header,
            LocalizedText.Get("code_editor.context.newfile"))), "Folder right-click exposes contextual file actions.");
        row.ContextMenu!.Close();
        var fileNode = root.Children.Single(node => node.Name == "app.cs");
        var fileRow = view.GetVisualDescendants().OfType<StackPanel>().Single(control => control.Name == "ProjectNodeHeader"
            && ReferenceEquals(control.DataContext, fileNode));
        RightClick(fileRow);
        Check(fileRow.ContextMenu is { IsOpen: true } && fileRow.ContextMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header,
            LocalizedText.Get("code_editor.context.rename"))), "File right-click exposes its own rename/delete actions.");
        fileRow.ContextMenu!.Close();
        host.Close();
    }
}
