using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.CodeEditor;
using RelaxKonOS.Client.Apps.Explorer;
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
