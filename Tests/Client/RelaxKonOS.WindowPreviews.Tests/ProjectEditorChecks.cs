using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RelaxKonOS.Client.Apps.CodeEditor;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Git;
using RelaxKonOS.Client.Apps.Git.Views;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Protocol.Desktop;

internal static class ProjectEditorChecks
{
    public static void Run(ShellSettings settings)
    {
        var task = RunAsync(settings);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("Project editor checks timed out.");
        task.GetAwaiter().GetResult();
    }

    private static async Task RunAsync(ShellSettings settings)
    {
        var files = DispatchProxy.Create<IExplorerClient, ProjectFiles>();
        var store = (ProjectFiles)(object)files;
        store.Files["/work/app.cs"] = Encoding.UTF8.GetBytes("class App {}\r\n");
        var api = DispatchProxy.Create<IRemoteGitClient, ProjectGit>();
        var remote = (ProjectGit)(object)api;
        var git = new GitClientViewModel(api);
        using var project = new ProjectEditorViewModel(git, api, files, true);
        // Mount while still in the picker: bindings must follow repository selection.
        var view = GitClientWorkspace.Create(git, project);
        var host = new Window { Width = 1180, Height = 760, Content = view };
        host.Show();
        Dispatcher.UIThread.RunJobs();
        var commitButton = view.FindControl<ToggleButton>("CommitToolButton")!;
        var gitButton = view.FindControl<ToggleButton>("GitToolButton")!;
        Check(!commitButton.IsEnabled && !gitButton.IsEnabled, "Git tools are disabled before opening a repository.");
        void ClickTool(ToggleButton button)
        {
            host.UpdateLayout();
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), host)!.Value;
            host.MouseDown(point, MouseButton.Left);
            host.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            host.UpdateLayout();
        }
        var repository = new GitRepositoryDto("repo", "Project", "/work");
        git.Repositories.Add(repository);
        git.IsStarting = false;
        git.HasLoadedRepositories = true;
        await git.OpenProjectCommand.ExecuteAsync(repository);
        Check(!git.IsPickerMode && project.Editor.WorkspaceRoots.Single().Path == "/work", "Opening a repository loads its full project tree.");
        Dispatcher.UIThread.RunJobs();
        Check(commitButton.IsEnabled && gitButton.IsEnabled, "Opening from the picker enables both Git tool buttons.");
        ClickTool(commitButton);
        Check(project.IsCommitVisible && view.FindControl<Border>("SidebarHost")!.IsVisible
            && view.FindControl<GitCommitToolView>("CommitTool")!.IsVisible,
            "A real pointer click opens the Commit tool below Project.");
        ClickTool(commitButton);
        Check(!project.IsCommitVisible, "Clicking Commit again collapses it.");
        git.IsBusy = true;
        Check(!git.CanManage && commitButton.IsEnabled && gitButton.IsEnabled,
            "Git operations disable management without disabling tool navigation.");
        ClickTool(gitButton);
        Check(project.IsGitVisible && view.FindControl<Border>("GitToolHost")!.IsVisible
            && view.FindControl<ContentControl>("ContentHost")!.Content is GitLogView,
            "A real pointer click opens the bottom Git log even while busy.");
        ClickTool(gitButton);
        Check(!project.IsGitVisible && !view.FindControl<Border>("GitToolHost")!.IsVisible,
            "Clicking Git again collapses the bottom tool.");
        git.IsBusy = false;
        await project.OpenWorkingTreeFileAsync(new("app.cs"));
        var document = project.Editor.ActiveDocument!;
        Check(document.Text.Contains("class App") && document.Version == GitTextFileWrite.Version(store.Files["/work/app.cs"]), "Editor reads a file with its exact byte version.");
        await project.OpenWorkingTreeFileAsync(new("../outside.cs"));
        Check(project.Editor.OpenDocuments.Count == 1, "Malformed Git paths cannot escape the project root.");

        project.Editor.Text = "class App { int X; }\r\n";
        project.RequestDraftActionAsync = _ => Task.FromResult(EditorDraftAction.Cancel);
        await git.CheckoutCommand.ExecuteAsync(new GitBranchDto("feature", "abc"));
        Check(remote.Checkouts == 0 && document.IsDirty, "Canceling the draft prompt prevents checkout and retains the buffer.");
        project.RequestDraftActionAsync = _ => Task.FromResult(EditorDraftAction.Save);
        await git.CheckoutCommand.ExecuteAsync(new GitBranchDto("feature", "abc"));
        Check(remote.Checkouts == 1 && store.Writes == 1 && !document.IsDirty, "Saving drafts precedes checkout and uses conditional file writes.");

        Check(git.EditorStagedFiles.Count == 1 && git.EditorUnstagedFiles.Count == 1
            && ReferenceEquals(git.EditorStagedFiles[0].Selection, git.EditorUnstagedFiles[0].Selection),
            "Index and worktree entries retain separate diffs and share one commit selection.");
        git.EditorStagedFiles[0].Selection!.IsSelected = true;
        Check(git.SelectedCount == 1 && git.EditorUnstagedFiles[0].Selection!.IsSelected, "Selecting either side selects one path, without duplication.");

        project.Editor.Text = "unsaved local draft";
        store.Files["/work/app.cs"] = Encoding.UTF8.GetBytes("external change");
        await project.Editor.SaveCommand.ExecuteAsync(null);
        Check(document.IsDirty && document.HasExternalChanges && document.Text == "unsaved local draft"
            && Encoding.UTF8.GetString(store.Files["/work/app.cs"]) == "external change", "A stale save preserves both the local draft and the external file.");
        project.RequestDraftActionAsync = _ => Task.FromResult(EditorDraftAction.Save);
        await git.CheckoutCommand.ExecuteAsync(new GitBranchDto("other", "def"));
        Check(remote.Checkouts == 1 && document.IsDirty, "A failed draft save blocks the dependent Git operation.");
        Check(await project.Editor.DiscardDocumentAsync(document) && project.Editor.Text == "external change", "Explicit discard reloads the latest file.");
        store.Files["/work/app.cs"] = Encoding.UTF8.GetBytes("new disk content");
        await project.Editor.RefreshOpenDocumentsAsync();
        Check(document.Text == "new disk content" && !document.IsDirty, "Clean documents refresh after an external edit.");
        project.Editor.Text = "keep this draft";
        store.Files.Remove("/work/app.cs");
        await project.Editor.RefreshOpenDocumentsAsync();
        Check(document.HasExternalChanges && document.Text == "keep this draft", "External deletion preserves the local draft.");
        store.Files["/work/deleted.cs"] = Encoding.UTF8.GetBytes("delete later");
        await project.Editor.OpenPathAsync("/work/deleted.cs");
        var deleted = project.Editor.ActiveDocument!;
        project.Editor.Text = "discard explicitly";
        store.Files.Remove("/work/deleted.cs");
        Check(await project.Editor.DiscardDocumentAsync(deleted) && !project.Editor.OpenDocuments.Contains(deleted),
            "Explicit discard can close a draft whose server file was deleted.");
        project.Editor.ActiveDocument = document;
        store.Files["/work/app.cs"] = Encoding.UTF8.GetBytes("restored");
        await project.Editor.DiscardDocumentAsync(document);

        foreach (var encoding in new[] { "utf-8", "utf-16le", "utf-16be", "utf-32le", "utf-32be" })
        {
            var path = $"/work/{encoding}.txt";
            store.Files[path] = TextFileCodec.Encode("a\r\n", encoding, true);
            await project.Editor.OpenPathAsync(path);
            project.Editor.Text += "b\r\n";
            await project.Editor.SaveCommand.ExecuteAsync(null);
            var decoded = TextFileCodec.Decode(path, store.Files[path]);
            Check(decoded.Bom && decoded.Encoding == encoding && decoded.Content == "a\r\nb\r\n",
                $"Editing and saving {encoding} retains its BOM and CRLF endings.");
        }
        project.Editor.ActiveDocument = document;

        await project.OpenDiffAsync(new("app.cs", Staged: true), null);
        await project.OpenDiffAsync(new("app.cs"), null);
        Check(project.Editor.OpenDocuments.Count(item => item.ContentKind == CodeEditorContentKind.Comparison) == 2
            && !project.Editor.CanSaveDocument, "Index and worktree comparisons open distinct read-only tabs.");
        var pending = new TaskCompletionSource<GitDiffDto>();
        remote.PendingDiff = pending;
        var oldDiff = project.OpenDiffAsync(new("old.cs"), null);
        remote.PendingDiff = null;
        await project.OpenDiffAsync(new("new.cs"), "abc12345");
        pending.SetResult(new("old.cs", "v", Patch: "old"));
        await oldDiff;
        Check(project.Editor.ActiveDocument!.DisplayName.StartsWith("new.cs"), "A late diff response cannot replace a newer comparison.");
        pending = new TaskCompletionSource<GitDiffDto>();
        remote.PendingDiff = pending;
        oldDiff = project.OpenDiffAsync(new("old.cs"), null);
        await project.OpenWorkingTreeFileAsync(new("app.cs"));
        remote.PendingDiff = null;
        pending.SetResult(new("old.cs", "v", Patch: "old"));
        await oldDiff;
        Check(ReferenceEquals(project.Editor.ActiveDocument, document), "Opening a source file supersedes a pending comparison response.");

        project.Editor.ActiveDocument = document;
        project.Editor.Text = "commit content";
        git.CommitMessage = "Save project";
        git.ShowPushDialogAsync = () => Task.FromResult(false);
        await git.CommitCommand.ExecuteAsync(null);
        Check(remote.LastCommit?.Paths.SequenceEqual(new[] { "app.cs" }) == true
            && Encoding.UTF8.GetString(store.Files["/work/app.cs"]) == "commit content", "Commit saves drafts and submits only explicitly selected disk paths.");

        var textEditor = view.EditorView.FindControl<AvaloniaEdit.TextEditor>("Editor")!;
        var firstBuffer = textEditor.Document;
        textEditor.CaretOffset = 4;
        textEditor.Document.Insert(textEditor.Document.TextLength, " edited");
        var firstText = document.Text;
        project.Editor.NewDocumentCommand.Execute(null);
        textEditor.Document.Insert(0, "scratch");
        textEditor.Document.UndoStack.Undo();
        Check(document.Text == firstText && project.Editor.Text == string.Empty, "Undo in another tab never changes the original document.");
        project.Editor.ActiveDocument = document;
        Check(ReferenceEquals(firstBuffer, textEditor.Document) && textEditor.CaretOffset == 4, "Each tab restores its own editor buffer and caret.");
        textEditor.Document.UndoStack.Undo();
        Check(document.Text == "commit content", "Switching tabs preserves the original document's undo history.");
        project.IsCommitVisible = true;
        project.IsGitVisible = true;
        var output = Path.Combine(AppContext.BaseDirectory, "preview-qa", "project-editor");
        Directory.CreateDirectory(output);
        foreach (var width in new[] { 1180, 860, 640 })
        {
            host.Width = width;
            Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
            Check(view.EditorView.IsVisible && view.EditorView.Bounds.Height > 100, $"Editor remains mounted at {width} DIP with Git tools open.");
            Check(view.FindControl<ContentControl>("ContentHost")!.Content is GitLogView, "The bottom Git tool contains the real log view.");
            Check(view.FindControl<Border>("GitToolHost")!.Bounds.Width > view.FindControl<Border>("SidebarHost")!.Bounds.Width,
                "The bottom Git tool spans beneath both the sidebar and editor.");
            Check(view.FindControl<Menu>("MainMenu")!.IsVisible == (width >= 900),
                "The menu collapses to one entry in compact windows.");
            var log = (GitLogView)view.FindControl<ContentControl>("ContentHost")!.Content!;
            Check(log.FindControl<Border>("BranchPane")!.IsVisible == (width - 38 >= 800),
                "Compact logs reserve space for commits and files while retaining the branch filter.");
            using var bitmap = new RenderTargetBitmap(new PixelSize(width, 760));
            bitmap.Render(host);
            bitmap.Save(Path.Combine(output, $"project-editor-{width}.png"), new PngBitmapEncoderOptions());
        }
        var originalLanguage = settings.Language;
        var originalAppearance = settings.Appearance;
        try
        {
            foreach (var language in new[] { "en-US", "zh-CN", "ja-JP" })
            {
                settings.Language = language;
                Dispatcher.UIThread.RunJobs();
                project.RefreshLocalization();
                Check(git.LogDateOptions[0].Label == LocalizedText.Get("git.editor.date_all")
                    && project.Editor.OpenDocuments.Any(item => item.ComparisonKey is not null
                        && item.DisplayName.EndsWith(LocalizedText.Get("git.editor.index"))), $"Dynamic filter and Diff labels switch to {language}.");
                foreach (var dark in new[] { false, true })
                {
                    settings.Appearance = settings.Appearance with { Mode = dark ? ThemeKind.Dark : ThemeKind.Light };
                    host.Width = 1180;
                    Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
                    using var bitmap = new RenderTargetBitmap(new PixelSize(1180, 760));
                    bitmap.Render(host);
                    bitmap.Save(Path.Combine(output, $"project-editor-{language}-{(dark ? "dark" : "light")}.png"), new PngBitmapEncoderOptions());
                    project.IsCommitVisible = false;
                    project.Editor.IsSidebarVisible = true;
                    project.Editor.SelectedFolderNode = project.Editor.WorkspaceRoots.FirstOrDefault();
                    Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
                    using var treeBitmap = new RenderTargetBitmap(new PixelSize(1180, 760));
                    treeBitmap.Render(host);
                    treeBitmap.Save(Path.Combine(output, $"project-tree-{language}-{(dark ? "dark" : "light")}.png"), new PngBitmapEncoderOptions());
                    project.IsCommitVisible = true;
                    git.IsPickerMode = true;
                    Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
                    using var pickerBitmap = new RenderTargetBitmap(new PixelSize(1180, 760));
                    pickerBitmap.Render(host);
                    pickerBitmap.Save(Path.Combine(output, $"project-picker-{language}-{(dark ? "dark" : "light")}.png"), new PngBitmapEncoderOptions());
                    git.IsPickerMode = false;
                    Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
                }
            }
        }
        finally
        {
            settings.Language = originalLanguage;
            settings.Appearance = originalAppearance;
            project.RefreshLocalization();
        }
        project.IsGitVisible = false;
        Check(project.Editor.OpenDocuments.Contains(document), "Hiding Git tools does not discard editor documents.");
        host.Close();

        var plainGit = new GitClientViewModel(api);
        using var plain = new ProjectEditorViewModel(plainGit, api, files, true);
        plainGit.ShowRemotePathPickerAsync = () => Task.FromResult<string?>("/work");
        await plainGit.OpenFolderCommand.ExecuteAsync(null);
        Check(!plainGit.IsPickerMode && plainGit.SelectedRepository is null && plain.ProjectPath == "/work" && remote.Initializations == 0,
            "An ordinary directory opens for editing without initializing or registering Git.");

        var slowRead = new TaskCompletionSource<byte[]?>();
        store.PendingRead = slowRead;
        var late = plain.Editor.OpenPathAsync("/work/late.cs");
        plain.Dispose();
        slowRead.SetResult(Encoding.UTF8.GetBytes("late"));
        await late;
        Check(plain.Editor.OpenDocuments.Count == 0, "Disposal prevents late file reads from reopening a tab.");
        await EditorContextChecks.RunAsync();
        Console.WriteLine("Project editor behavior and layout checks passed.");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("PASS PROJECT: " + message);
    }
}

public class ProjectFiles : DispatchProxy, IEditorFileWriter
{
    public Dictionary<string, byte[]> Files { get; } = [];
    public int Writes { get; private set; }
    public HashSet<string> Directories { get; } = ["/work"];
    public bool FailMutation { get; set; }
    private static string ParentPath(string path) => path[..path.LastIndexOf('/')];
    private static FileSystemEntryDto Entry(string path, bool directory) => new(path, path.Split('/').Last(), null,
        directory ? FileSystemEntryType.Directory : FileSystemEntryType.File, null, null, null, false, false, null);
    public TaskCompletionSource<byte[]?>? PendingRead { get; set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var path = args?[0] as string ?? "/work";
        if (method!.Name == nameof(IExplorerClient.CreateDirectoryAsync))
        {
            if (FailMutation || !Directories.Add(path)) throw new IOException("Cannot create directory");
            return Task.FromResult(Entry(path, true));
        }
        if (method.Name == nameof(IExplorerClient.RenameAsync))
        {
            if (FailMutation) throw new IOException("Cannot rename");
            var target = ParentPath(path) + "/" + (string)args![1]!;
            if (Files.ContainsKey(target) || Directories.Contains(target)) throw new IOException("Already exists");
            var directory = Directories.Contains(path);
            foreach (var key in Files.Keys.Where(key => key == path || key.StartsWith(path + "/")).ToArray())
            { Files[target + key[path.Length..]] = Files[key]; Files.Remove(key); }
            foreach (var key in Directories.Where(key => key == path || key.StartsWith(path + "/")).ToArray())
            { Directories.Remove(key); Directories.Add(target + key[path.Length..]); }
            return Task.FromResult(Entry(target, directory));
        }
        if (method.Name == nameof(IExplorerClient.DeleteAsync))
        {
            if (FailMutation) throw new IOException("Cannot delete");
            foreach (var key in Files.Keys.Where(key => key == path || key.StartsWith(path + "/")).ToArray()) Files.Remove(key);
            Directories.RemoveWhere(key => key == path || key.StartsWith(path + "/"));
            return Task.CompletedTask;
        }
        return method.Name switch
        {
            nameof(IExplorerClient.GetInfoAsync) => Task.FromResult<FileSystemEntryDto?>(Files.ContainsKey(path) || Directories.Contains(path) ? Entry(path, Directories.Contains(path)) : null),
            nameof(IExplorerClient.ReadFileAsync) => PendingRead?.Task ?? Task.FromResult(Files.GetValueOrDefault(path)),
            nameof(IExplorerClient.GetDirectoryAsync) => Task.FromResult(new DirectoryDto(path, "Project", FileSystemEntryType.Directory, Directories.Where(item => item != path && ParentPath(item) == path).Select(item => Entry(item, true)).ToArray(),
                Files.Where(item => ParentPath(item.Key) == path).Select(item => new FileEntryDto(item.Key, item.Key.Split('/').Last(), ".cs", item.Value.Length, null, null, null, false, false, "text/plain")).ToArray(), null, null)),
            _ => throw new NotSupportedException(method.Name),
        };
    }
    public Task<string> WriteEditorFileAsync(string path, byte[] content, string? expectedVersion, CancellationToken ct = default)
    {
        var actual = Files.TryGetValue(path, out var bytes) ? GitTextFileWrite.Version(bytes) : null;
        if (actual != expectedVersion) throw new RelaxKonOSAuthException(new ProblemDetails(
            "https://relaxkonos.app/problems/text-file-changed", "Changed", 409, null, null));
        Files[path] = content;
        Writes++;
        return Task.FromResult(GitTextFileWrite.Version(content));
    }
}

public class ProjectGit : DispatchProxy
{
    public int Checkouts { get; private set; }
    public int Initializations { get; private set; }
    public GitCommitRequest? LastCommit { get; private set; }
    public TaskCompletionSource<GitDiffDto>? PendingDiff { get; set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case nameof(IRemoteGitClient.GetStatusAsync): return Task.FromResult(new GitStatusDto("main", [new("app.cs", Staged: true)], [new("app.cs")], [], [], "v"));
            case nameof(IRemoteGitClient.ListBranchesAsync): return Task.FromResult<IReadOnlyList<GitBranchDto>>([new("main", "abc", IsCurrent: true)]);
            case nameof(IRemoteGitClient.GetLogAsync): return Task.FromResult<IReadOnlyList<GitCommitDto>>([]);
            case nameof(IRemoteGitClient.ListRemotesAsync): return Task.FromResult<IReadOnlyList<GitRemoteDto>>([]);
            case nameof(IRemoteGitClient.GetConflictStateAsync): return Task.FromResult(new GitConflictStateDto(null, []));
            case nameof(IRemoteGitClient.GetDiffAsync): return PendingDiff?.Task ?? Task.FromResult(new GitDiffDto((string)args![1]!, "v", Patch: "@@ -1 +1 @@\n-old\n+new\n"));
            case nameof(IRemoteGitClient.CheckoutAsync): Checkouts++; return Task.FromResult(new GitOperationResult(true));
            case nameof(IRemoteGitClient.CommitAsync): LastCommit = (GitCommitRequest)args![1]!; return Task.FromResult(new GitOperationResult(true));
            case nameof(IRemoteGitClient.ProbeRepositoryAsync): return Task.FromResult(new GitRepositoryProbeDto(false));
            case nameof(IRemoteGitClient.InitRepositoryAsync): Initializations++; return Task.FromResult(new GitOperationResult(true));
            default: throw new NotSupportedException(method.Name);
        }
    }
}
