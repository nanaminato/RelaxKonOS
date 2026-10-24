using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.CodeEditor;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Client.Apps.Notepad;
using RelaxKonOS.Protocol.Files;
using IOPath = System.IO.Path;

/// <summary>
/// Verifies the desktop UI review's P0-2 editor batch: the text and code editors report unsaved
/// state to the window close guard, freeze the editor while a write is in flight, keep their draft
/// when a read fails, and ignore a read that finishes after the document already changed.
/// </summary>
internal static class EditorDraftStateChecks
{
    public static void Run()
    {
        Notepad();
        CodeEditor();
        CodeEditorPreviews();
        Console.WriteLine("Editor draft state checks passed.");
    }

    private static void Notepad()
    {
        var files = new RecordingExplorerClient();
        var notepad = new NotepadViewModel(files, "UTF-8");
        var view = new NotepadView { DataContext = notepad };
        var host = new Window { Width = 900, Height = 600, Content = view };
        host.Show();
        Dispatcher.UIThread.RunJobs();
        var editor = view.GetVisualDescendants().OfType<TextBox>().Single();
        var menu = view.GetVisualDescendants().OfType<Menu>().Single();

        Check(!notepad.IsDirty && notepad.CanEditDocument, "A fresh Notepad document is clean and editable.");

        notepad.Text = "draft";
        Check(notepad.IsDirty, "Editing marks the document dirty so the close guard asks before discarding.");

        // A failed read must not clear what the user already has on screen.
        files.ReadFailure = new IOException("offline");
        _ = notepad.OpenPathAsync("/remote/notes.txt");
        Dispatcher.UIThread.RunJobs();
        Check(notepad.Text == "draft" && notepad.CurrentPath is null,
            "A failed read keeps the editor draft.");

        files.ReadFailure = null;
        files.ReadResult = "remote content"u8.ToArray();
        _ = notepad.OpenPathAsync("/remote/notes.txt");
        Dispatcher.UIThread.RunJobs();
        Check(notepad.Text == "remote content" && notepad.CurrentPath == "/remote/notes.txt" && !notepad.IsDirty,
            "A successful read adopts the file and clears the dirty flag.");

        // A write in flight freezes the editor and the command surface.
        var write = new TaskCompletionSource<bool>();
        files.PendingWrite = write;
        notepad.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Check(notepad.IsSaving && !notepad.CanEditDocument, "Starting a save locks the document.");
        Check(editor.IsReadOnly && !menu.IsEnabled,
            "The editor is read-only and the menu is disabled while saving.");
        notepad.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Check(files.WriteCount == 1, "A repeat save while writing does not send a second request.");

        write.SetResult(true);
        Pump(() => !notepad.IsSaving);
        Check(!notepad.IsDirty && notepad.CurrentPath == "/remote/notes.txt",
            "A completed save clears the lock and the dirty flag.");

        // An unconfirmed write leaves the document dirty so the user verifies before retrying.
        files.PendingWrite = null;
        files.WriteFailure = new IOException("rejected");
        notepad.Text = "changed again";
        notepad.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Check(notepad.IsDirty && !notepad.IsSaving,
            "An unconfirmed write leaves the document dirty.");

        // A read that finishes after the user started a new document must not replace it.
        files.WriteFailure = null;
        var read = new TaskCompletionSource<byte[]?>();
        files.PendingRead = read;
        _ = notepad.OpenPathAsync("/remote/slow.txt");
        notepad.NewDocumentCommand.Execute(null);
        read.SetResult("late content"u8.ToArray());
        Dispatcher.UIThread.RunJobs();
        Check(notepad.Text.Length == 0 && notepad.CurrentPath is null,
            "A late read does not replace the document the user started meanwhile.");

        host.Close();
    }

    private static void CodeEditor()
    {
        var files = new RecordingExplorerClient();
        var code = new CodeEditorViewModel(files, pathCaseSensitive: true, "UTF-8");

        code.NewDocumentCommand.Execute(null);
        Check(!code.HasUnsavedDocuments && code.CanEditDocument,
            "A new Code Editor tab starts clean and editable.");

        // A tab's mark is derived from the document's own name, so the same file carries the same
        // mark on the tab that has it open as in the file browser's list.
        Check(new CodeEditorDocument(null, string.Empty, "UTF-8", "Untitled 1").Kind == ExplorerFileIconKind.File,
            "An untitled editor tab is marked as a generic file.");
        Check(new CodeEditorDocument("/remote/app.cs", string.Empty, "UTF-8", "Untitled 1").Kind == ExplorerFileIconKind.Code,
            "A source file editor tab is marked as code.");
        var renamed = new CodeEditorDocument(null, string.Empty, "UTF-8", "Untitled 1");
        renamed.Path = "/remote/notes.md";
        Check(renamed.Kind == ExplorerFileIconKind.Document && renamed.DisplayName == "notes.md",
            "Saving a draft under a name re-marks its tab.");

        code.Text = "class C {}";
        Check(code.HasUnsavedDocuments,
            "Editing a tab is reported as unsaved state for the window close guard.");

        var dirty = code.OpenDocuments.Single(document => document.IsDirty);
        code.RequestDiscardChangesAsync = _ => Task.FromResult(false);
        code.CloseDocumentCommand.Execute(dirty);
        Dispatcher.UIThread.RunJobs();
        Check(code.OpenDocuments.Contains(dirty), "Declining to discard keeps the dirty tab open.");

        code.NewDocumentCommand.Execute(null);
        Check(code.OpenDocuments.Count == 2 && code.HasUnsavedDocuments,
            "A second dirty tab also counts as unsaved state.");

        // A write in flight locks the window and, once confirmed, clears only its own tab.
        var saver = new CodeEditorViewModel(files, pathCaseSensitive: true, "UTF-8")
        {
            RequestSavePathAsync = _ => Task.FromResult<string?>("/remote/app.cs"),
        };
        saver.NewDocumentCommand.Execute(null);
        saver.Text = "class C {}";
        var write = new TaskCompletionSource<bool>();
        files.PendingWrite = write;
        saver.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Check(saver.IsSaving && !saver.CanEditDocument, "A Code Editor write locks the window.");
        saver.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Check(files.WriteCount == 1, "A repeat Code Editor save while writing does not send a second request.");

        write.SetResult(true);
        Pump(() => !saver.IsSaving);
        Check(!saver.HasUnsavedDocuments && saver.CurrentPath == "/remote/app.cs",
            "A completed write clears the dirty tab and records the fixed target.");
    }

    private static void CodeEditorPreviews()
    {
        var files = new RecordingExplorerClient();
        using var code = new CodeEditorViewModel(files);
        var view = new CodeEditorView { DataContext = code };
        var host = new Window { Width = 960, Height = 640, Content = view };
        try
        {
            var root = new CodeEditorFolderNode("workspace", "/remote", true) { IsLoaded = true, IsExpanded = true };
            root.Children.Clear();
            foreach (var name in new[] { "app.cs", "photo.png", "logo.svg", "package.zip", "README.md" })
                root.Children.Add(new CodeEditorFolderNode(name, "/remote/" + name, false));
            code.WorkspaceRoots.Add(root);
            host.Show(); Dispatcher.UIThread.RunJobs();
            code.NewDocumentCommand.Execute(null);
            code.Text = "keep my draft";
            var draft = code.ActiveDocument!;
            using var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(64, 32),
                new Avalonia.Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            using var stream = new MemoryStream();
            bitmap.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            files.ReadResult = stream.ToArray();
            code.OpenPathAsync("/remote/photo.png").GetAwaiter().GetResult(); Dispatcher.UIThread.RunJobs();
            Check(code.IsImageDocument && code.PreviewImage?.Size == new Avalonia.Size(64, 32)
                && code.Text == "" && !code.ActiveDocument!.IsDirty,
                "PNG opens as an image tab without decoding binary bytes into text.");
            Check(!code.SaveCommand.CanExecute(null) && !code.SaveAsCommand.CanExecute(null)
                && !code.ChooseEncodingCommand.CanExecute(null), "Image previews cannot be overwritten through text commands.");
            code.SaveCommand.Execute(null);
            Check(files.WriteCount == 0, "Executing Save on an image preview does not write empty text.");
            Check(!view.FindControl<AvaloniaEdit.TextEditor>("Editor")!.IsVisible,
                "Image tabs hide the text editing surface.");
            var imageDocument = code.ActiveDocument!;
            code.ActiveDocument = draft;
            Check(code.IsTextDocument && code.Text == "keep my draft" && draft.IsDirty,
                "Switching back from a preview preserves the dirty text draft.");
            code.ActiveDocument = imageDocument;
            Check(code.PreviewImage is not null && !code.IsDirty, "Switching tabs preserves the image preview.");

            files.ReadResult = System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='120' height='80'><rect width='120' height='80' fill='#387dec'/></svg>");
            code.OpenPathAsync("/remote/logo.svg").GetAwaiter().GetResult(); Dispatcher.UIThread.RunJobs();
            Check(code.IsImageDocument && code.HasImageSourceText, "SVG opens with a preview and an editable source entry.");
            code.ToggleImageSourceCommand.Execute(null);
            code.Text = code.Text.Replace("#387dec", "#ef873e");
            Check(code.IsTextDocument && code.IsDirty && code.SaveCommand.CanExecute(null), "SVG source remains editable and tracks unsaved changes.");
            code.ToggleImageSourceCommand.Execute(null);
            Check(code.IsImageDocument && code.IsDirty, "Returning to SVG preview renders edits without discarding the dirty flag.");
            Capture("image");
            code.ToggleImageSourceCommand.Execute(null);
            code.Text = "not valid SVG";
            code.ToggleImageSourceCommand.Execute(null);
            Check(code.IsTextDocument && code.Text == "not valid SVG", "A failed SVG render preserves editable source.");
            var svgDocument = code.ActiveDocument!;
            code.ActiveDocument = draft;
            code.ActiveDocument = svgDocument;
            Check(code.IsTextDocument && code.Text == "not valid SVG" && code.IsDirty,
                "SVG source mode and unsaved edits survive switching away and back.");

            files.ReadResult = [0x50, 0x4b, 3, 4, 0, 0, 1, 2];
            code.OpenPathAsync("/remote/package.zip").GetAwaiter().GetResult(); Dispatcher.UIThread.RunJobs();
            Check(code.IsBinaryDocument && code.Text == "" && !code.SaveCommand.CanExecute(null),
                "Binary files show a read-only information card instead of garbage text.");
            Capture("binary");
            code.OpenPathAsync("/remote/unknown").GetAwaiter().GetResult();
            Check(code.IsBinaryDocument, "Extensionless binary data is also kept out of the text editor.");
            code.OpenPathAsync("/remote/broken.png").GetAwaiter().GetResult();
            Check(code.IsBinaryDocument && code.Text == "", "A damaged image shows an information card instead of binary text.");
            files.ReadResult = System.Text.Encoding.UTF8.GetBytes("<svg unfinished");
            code.OpenPathAsync("/remote/broken.svg").GetAwaiter().GetResult();
            Check(code.IsTextDocument && code.Text == "<svg unfinished", "A damaged SVG can still be repaired as source text.");
            files.ReadResult = [0xff, 0xfe, 0x41, 0, 0x42, 0];
            code.OpenPathAsync("/remote/unicode.txt").GetAwaiter().GetResult();
            Check(code.IsTextDocument && code.Text == "AB" && code.EncodingName == "UTF-16 LE",
                "UTF-16 BOM text is recognized before checking for binary control characters.");
            Check(new CodeEditorFolderNode("photo.png", "/photo.png", false).Kind == ExplorerFileIconKind.Image
                && new CodeEditorFolderNode("app.cs", "/app.cs", false).Kind == ExplorerFileIconKind.Code
                && new CodeEditorFolderNode("src", "/src", true).Kind == ExplorerFileIconKind.Folder,
                "Workspace files and folders use the same type icons as Explorer.");
            files.PendingRead = new TaskCompletionSource<byte[]?>();
            var pendingImage = code.OpenPathAsync("/remote/late.png");
            code.NewDocumentCommand.Execute(null);
            code.Text = "newer draft";
            var count = code.OpenDocuments.Count;
            files.PendingRead.SetResult(stream.ToArray());
            pendingImage.GetAwaiter().GetResult();
            Check(code.IsTextDocument && code.Text == "newer draft" && code.OpenDocuments.Count == count,
                "A late image read cannot replace a newer draft or leave a phantom preview tab.");
        }
        finally { host.Close(); }

        void Capture(string name)
        {
            var directory = IOPath.Combine(AppContext.BaseDirectory, "preview-qa", "code-editor");
            Directory.CreateDirectory(directory);
            foreach (var width in new[] { 960, 320 })
            {
                host.Width = width;
                Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
                using var rendered = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(width, 640));
                rendered.Render(host);
                rendered.Save(IOPath.Combine(directory, $"{name}-{width}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            host.Width = 960; Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Pump(Func<bool> condition)
    {
        if (!SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return condition(); }, 2000))
            throw new Exception("Editor state did not settle.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    /// <summary>Minimal file client: only reads and writes are exercised by these editors.</summary>
    private sealed class RecordingExplorerClient : IExplorerClient
    {
        public byte[]? ReadResult { get; set; }
        public Exception? ReadFailure { get; set; }
        public TaskCompletionSource<byte[]?>? PendingRead { get; set; }
        public TaskCompletionSource<bool>? PendingWrite { get; set; }
        public Exception? WriteFailure { get; set; }
        public int WriteCount { get; private set; }

        public Task<byte[]?> ReadFileAsync(string path, CancellationToken ct = default)
        {
            if (PendingRead is { } gate) return gate.Task;
            return ReadFailure is { } failure
                ? Task.FromException<byte[]?>(failure)
                : Task.FromResult(ReadResult);
        }

        public async Task<FileEntryDto> WriteFileAsync(string path, byte[] content, CancellationToken ct = default)
        {
            WriteCount++;
            if (PendingWrite is { } gate) await gate.Task;
            if (WriteFailure is { } failure) throw failure;
            return new FileEntryDto(path, IOPath.GetFileName(path), IOPath.GetExtension(path), content.Length,
                null, null, null, false, false, "text/plain");
        }

        public Task<FileOperationDto> StartOperationAsync(StartFileOperationRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<FileOperationDto>> ListOperationsAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<FileOperationDto> GetOperationAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<FileOperationDto> CancelOperationAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<FileOperationDto> DecideOperationAsync(Guid id, FileOperationDecisionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<DriveDto>> GetDrivesAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<SpecialLocationDto>> GetSpecialLocationsAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<DirectoryDto> GetDirectoryAsync(string? path, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<FileSystemEntryDto?> GetInfoAsync(string path, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<(Stream Stream, string FileName)?> DownloadAsync(string path, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<FileElevationResult> ElevateFileAccessAsync(string path, FileElevationCapability capability, string? password = null,
            string? administratorUsername = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<FileElevationResult> ElevateFileOperationAsync(IReadOnlyList<string> directoryPaths, FileElevationCapability capability,
            string? password = null, string? administratorUsername = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<FilePropertiesDto?> GetPropertiesAsync(string path, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<FilePropertiesDto> SetUnixOwnershipAsync(string path, string owner, string group, bool recursive, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<FilePropertiesDto> SetUnixPermissionsAsync(string path, int unixMode, bool recursive, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<FileSystemEntryDto> CreateDirectoryAsync(string path, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeleteAsync(string path, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<FileSystemEntryDto> RenameAsync(string sourcePath, string newName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<FileSystemEntryDto> MoveAsync(string sourcePath, string destinationPath, bool overwrite = false, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<FileSystemEntryDto> CopyAsync(string sourcePath, string destinationPath, bool overwrite = false, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<FileEntryDto> UploadAsync(string targetDirectoryPath, string fileName, Stream content,
            IProgress<long>? progress = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
