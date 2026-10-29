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
        LocalizationResourceChecks.Run(settings);
        Check(GitHostingLinks.Resolve([new("origin", "git@github.com:owner/repo.git")], "github", "pulls")?.AbsoluteUri == "https://github.com/owner/repo/pulls",
            "GitHub SSH remotes map to real pull request pages.");
        Check(GitHostingLinks.Resolve([new("origin", "git@ssh.dev.azure.com:v3/org/project/repo")], "azure", "repo")?.AbsoluteUri == "https://dev.azure.com/org/project/_git/repo",
            "Azure SSH remotes map to their HTTPS repository page.");
        Check(GitHostingLinks.Resolve([new("origin", "https://user:secret@gitlab.com/group/repo.git")], "gitlab", "pulls")?.AbsoluteUri == "https://gitlab.com/group/repo/-/merge_requests",
            "Hosting links never carry clone credentials to the browser.");
        Check(GitHostingLinks.Resolve([new("origin", "ssh://git@gitee.com/owner/repo.git")], "gitee", "pulls")?.AbsoluteUri == "https://gitee.com/owner/repo/pulls",
            "Gitee SSH remotes map to their pull request page.");
        Check(GitHostingLinks.Resolve([new("origin", "https://user:secret@gitee.com/owner/repo.git?token=private#fragment")], "gitee", "issues")?.AbsoluteUri == "https://gitee.com/owner/repo/issues",
            "Gitee issue links remove credentials, query strings and fragments.");
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
        var badge = view.FindControl<Button>("BranchBadge")!;
        remote.FailMaintenanceReads = true;
        var maintenanceReads = remote.MaintenanceReads;
        badge.Flyout!.ShowAt(badge);
        Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
        var branchPopup = view.FindControl<GitHeaderBranchPopup>("BranchPopup")!;
        await branchPopup.RefreshAsync();
        Check(remote.MaintenanceReads == maintenanceReads
            && git.Tags.Any(tag => tag.Name == "v1-preview") && git.Branches.Any(branch => branch.IsCurrent),
            "Opening the header refreshes branches and tags without requesting unavailable stash or worktree endpoints.");
        remote.FailMaintenanceReads = false;
        var mainNode = new GitHeaderBranchNode("main", git.Branches.First());
        var operations = branchPopup.BuildBranchMenu(mainNode).Items.OfType<MenuItem>().ToArray();
        Check(operations.Length == 11 && operations.Where(item => item.Command is not null)
            .All(item => ReferenceEquals(item.CommandParameter, mainNode.Branch)),
            "Branch actions bind their explicit clicked branch through both popup levels.");
        Check(!operations[0].IsEnabled && !operations[^2].IsEnabled,
            "The current branch cannot be checked out again or deleted from the header.");
        git.Branches.Add(new("feature/git-diff-view", "abc", Tracking: "origin/feature/git-diff-view"));
        git.Branches.Add(new("origin/feature/git-diff-view", "abc", IsRemote: true));
        branchPopup.Rebuild();
        Check(branchPopup.Nodes.Any(node => node.Name == LocalizedText.Get("git.workspace.local") && node.Children.Any(child => child.Name == "feature"))
            && branchPopup.Nodes.Any(node => node.Name == LocalizedText.Get("git.workspace.remote") && node.Children.Single().Name == "origin"),
            "The branch popup groups local paths and remote names without flattening slash-separated branches.");
        branchPopup.FindControl<TextBox>("BranchSearch")!.Text = "git-diff";
        Dispatcher.UIThread.RunJobs();
        Check(branchPopup.Nodes.SelectMany(node => node.Children).All(node => node.Name != "main"), "Branch search excludes nonmatching branches.");
        branchPopup.FindControl<TextBox>("BranchSearch")!.Text = "";
        badge.Flyout.Hide();
        Popup? gitMenuPopup = null;
        var detailedGitMenu = view.FindControl<MenuItem>("GitMenu")!;
        detailedGitMenu.TemplateApplied += (_, args) => gitMenuPopup = args.NameScope.Find<Popup>("PART_Popup");
        var output = Path.Combine(AppContext.BaseDirectory, "preview-qa", "project-editor");
        Directory.CreateDirectory(output);
        void SavePopup(Control popup, string name, double maximumHeight = 620)
        {
            Check(popup.Bounds.Width > 100 && popup.Bounds.Height > 80 && popup.Bounds.Height <= maximumHeight,
                "Header popup stays within its measured viewport.");
            using var image = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(popup.Bounds.Width), (int)Math.Ceiling(popup.Bounds.Height)));
            image.Render(popup); image.Save(Path.Combine(output, name), new PngBitmapEncoderOptions());
        }
        foreach (var width in new[] { 1180, 860, 640 })
        {
            host.Width = width;
            Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
            Check(view.EditorView.IsVisible && view.EditorView.Bounds.Height > 100, $"Editor remains mounted at {width} DIP with Git tools open.");
            Check(view.FindControl<ContentControl>("ContentHost")!.Content is GitLogView, "The bottom Git tool contains the real log view.");
            Check(view.FindControl<Border>("GitToolHost")!.Bounds.Width > view.FindControl<Border>("SidebarHost")!.Bounds.Width,
                "The bottom Git tool spans beneath both the sidebar and editor.");
            var menu = view.FindControl<Menu>("MainMenu")!;
            var expand = view.FindControl<Button>("CompactMenuButton")!;
            Check(!menu.IsVisible && expand.IsVisible, "All widths start with the brief menu.");
            var menuPoint = expand.TranslatePoint(new Point(expand.Bounds.Width / 2, expand.Bounds.Height / 2), host)!.Value;
            host.MouseDown(menuPoint, MouseButton.Left);
            host.MouseUp(menuPoint, MouseButton.Left);
            Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
            Check(menu.IsVisible && !expand.IsVisible, "Clicking the brief menu expands the detailed menu.");
            var fileMenu = view.FindControl<MenuItem>("FileMenu")!;
            fileMenu.IsSubMenuOpen = true;
            host.MouseMove(new Point(width - 20, 400));
            await Task.Delay(750);
            Check(menu.IsVisible, "An open submenu keeps the detailed menu available outside the header.");
            fileMenu.IsSubMenuOpen = false;
            await Task.Delay(750);
            Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
            Check(!menu.IsVisible && expand.IsVisible, "Leaving the detailed menu restores the brief menu after submenus close.");
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
                    badge.Flyout!.ShowAt(badge);
                    await branchPopup.RefreshAsync();
                    Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
                    branchPopup.UpdateLayout();
                    SavePopup(branchPopup, $"header-branches-{language}-{(dark ? "dark" : "light")}.png");
                    badge.Flyout.Hide();
                    var projectSwitcher = view.FindControl<Button>("ProjectSwitcher")!;
                    projectSwitcher.Flyout!.ShowAt(projectSwitcher);
                    Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
                    var projectPopup = view.FindControl<GitHeaderProjectPopup>("ProjectPopup")!;
                    projectPopup.Rebuild(); projectPopup.UpdateLayout();
                    SavePopup(projectPopup, $"header-projects-{language}-{(dark ? "dark" : "light")}.png");
                    projectSwitcher.Flyout.Hide();
                    view.FindControl<Button>("CompactMenuButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
                    detailedGitMenu.IsSubMenuOpen = true;
                    Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
                    Check(gitMenuPopup?.Child is not null, "The detailed Git menu uses its real popup presenter.");
                    SavePopup(gitMenuPopup!.Child!, $"header-git-menu-{language}-{(dark ? "dark" : "light")}.png", 1080);
                    detailedGitMenu.IsSubMenuOpen = false;
                    host.MouseMove(new Point(1100, 400));
                    await Task.Delay(700);
                    Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
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
        var other = new GitRepositoryDto("other", "Other", "/other");
        store.Directories.Add("/other"); git.Repositories.Add(other);
        project.Editor.ActiveDocument = document;
        project.Editor.Text += " dirty";
        project.RequestDraftActionAsync = _ => Task.FromResult(EditorDraftAction.Cancel);
        await git.OpenProjectCommand.ExecuteAsync(other);
        Check(git.SelectedRepository?.Id == repository.Id && project.ProjectPath == "/work" && project.Editor.HasUnsavedDocuments,
            "Canceling project switching preserves the current repository, buffers and project tree.");
        project.RequestDraftActionAsync = _ => Task.FromResult(EditorDraftAction.Discard);
        await git.OpenProjectCommand.ExecuteAsync(other);
        Check(git.SelectedRepository?.Id == other.Id && project.ProjectPath == "/other", "Accepted project switching opens the target project directly.");
        var projectsPopup = view.FindControl<GitHeaderProjectPopup>("ProjectPopup")!;
        projectsPopup.Rebuild();
        Check(projectsPopup.RecentProjects.Any(item => item.Path == "/work") && projectsPopup.RecentProjects.First().Path == "/other",
            "Project switcher remembers session history and the current project in the right order.");
        git.ShowWorkspaceInputAsync = (_, _) => Task.FromResult<string[]?>(["release/test", "HEAD"]);
        await git.NewTagCommand.ExecuteAsync(null);
        Check(remote.LastWorkspaceRequest is GitTagCreateRequest { Name: "release/test", Reference: "HEAD" }, "New tag menu submits its actual input values.");
        var priorRequest = remote.LastWorkspaceRequest;
        var pendingInput = new TaskCompletionSource<string[]?>();
        git.ShowWorkspaceInputAsync = (_, _) => pendingInput.Task;
        var pendingTag = git.NewTagCommand.ExecuteAsync(null);
        git.SelectedRepository = repository;
        pendingInput.SetResult(["wrong-project", "HEAD"]);
        await pendingTag;
        Check(ReferenceEquals(priorRequest, remote.LastWorkspaceRequest), "A delayed operation dialog cannot mutate a different selected repository.");
        await git.OpenProjectCommand.ExecuteAsync(repository);
        await git.ExportWorkingPatchCommand.ExecuteAsync(false);
        Check(project.Editor.ActiveDocument is { IsDirty: true, Path: null } && project.Editor.Text.Contains("exported patch"),
            "Patch export opens an editable untitled document that can be saved as a patch.");
        git.ShowWorkspaceInputAsync = (_, _) => Task.FromResult<string[]?>(["https://example.invalid/project.git", "/cloned"]);
        store.Directories.Add("/cloned");
        git.ShowGitCredentialsDialogAsync = (_, _) => Task.FromResult<GitCredentialRequest?>(new("user", "test-password", false));
        await git.CloneRepositoryCommand.ExecuteAsync(null);
        Check(remote.CloneRequests.Count == 2 && remote.CloneRequests[0].Credentials is null
            && remote.CloneRequests[1].Credentials?.Username == "user" && project.ProjectPath == "/cloned",
            "Clone retries with user-supplied credentials and opens its registered result.");
        remote.Remotes = [new("origin", "https://github.com/org/repo"), new("mirror", "https://gitee.com/org/repo")];
        var promptedHosts = new List<string?>();
        git.ShowGitCredentialsDialogAsync = (_, hostName) =>
        {
            promptedHosts.Add(hostName);
            return Task.FromResult<GitCredentialRequest?>(new("user", "one-operation-token", false));
        };
        await git.FetchCommand.ExecuteAsync(null);
        Check(remote.FetchRequests.Select(x => x.Remote).SequenceEqual(new[] { "origin", "origin", "mirror", "mirror" })
            && promptedHosts.SequenceEqual(new[] { "github.com", "gitee.com" }),
            "Fetch all handles each remote separately and prompts with its effective HTTPS host.");
        Check(remote.FetchRequests.Where(x => x.Credentials is not null).All(x => !x.Credentials!.SaveCredentials),
            "Fetch retries preserve the user's save opt-out.");
        git.ShowPullDialogAsync = () => Task.FromResult<GitPullRequest?>(new(Remote: "mirror"));
        await git.PullCommand.ExecuteAsync(null);
        Check(remote.PullRequests.Count == 2 && remote.PullRequests.All(x => x.Remote == "mirror")
            && remote.PullRequests[1].Credentials?.Password == "one-operation-token", "Pull retries exactly the selected remote with the entered credential.");
        var beforeCancel = remote.PullRequests.Count;
        git.ShowGitCredentialsDialogAsync = (_, _) => Task.FromResult<GitCredentialRequest?>(null);
        await git.PullCommand.ExecuteAsync(null);
        Check(remote.PullRequests.Count == beforeCancel + 1, "Canceling authentication does not repeat the network operation.");
        git.CredentialHost = "gitlab.example.com:8443"; git.CredentialUsername = "alice"; git.CredentialToken = "new-account-token";
        await git.SaveCredentialAccountCommand.ExecuteAsync(null);
        Check(git.CredentialToken.Length == 0 && git.CredentialAccounts.Single().Username == "alice", "Account save reloads public metadata and clears the token input.");
        await git.DefaultCredentialAccountCommand.ExecuteAsync(null);
        Check(remote.AccountRequests.Last().Password is null && remote.AccountRequests.Last().IsDefault, "Setting a default never requests or resubmits an existing token.");
        remote.RejectAccountSave = true; git.CredentialToken = "failed-token";
        await git.SaveCredentialAccountCommand.ExecuteAsync(null);
        Check(git.CredentialToken.Length == 0 && !git.CredentialAccountsBusy && git.CredentialAccountsMessage.Contains("rejected"), "A failed save clears the token and leaves the account manager usable.");
        remote.RejectAccountSave = false;
        git.ShowConfirmAsync = _ => Task.FromResult(true);
        await git.DeleteCredentialAccountCommand.ExecuteAsync(null);
        Check(git.CredentialAccounts.Count == 0 && git.CredentialToken.Length == 0, "Deleting an account removes its metadata from the manager.");
        remote.Remotes = [];

        var pendingStatus = new TaskCompletionSource<GitStatusDto>();
        remote.PendingStatuses[repository.Id] = pendingStatus;
        var openingFirst = git.OpenProjectCommand.ExecuteAsync(repository);
        var openingSecond = git.OpenProjectCommand.ExecuteAsync(other);
        pendingStatus.SetResult(new GitStatusDto("old-branch", [], [], [], [], "v"));
        await Task.WhenAll(openingFirst, openingSecond);
        remote.PendingStatuses.Remove(repository.Id);
        Check(git.SelectedRepository?.Id == other.Id && git.Status?.Branch == "main" && git.StatusText.ToString().Contains("Other"),
            "Late project responses cannot replace the newly selected project's status or title.");
        host.Close();

        var plainGit = new GitClientViewModel(api);
        using var plain = new ProjectEditorViewModel(plainGit, api, files, true);
        var pickCount = 0;
        plainGit.ShowRemotePathPickerAsync = () => { pickCount++; return Task.FromResult<string?>("/work"); };
        plainGit.IsBusy = true;
        await plainGit.OpenFolderCommand.ExecuteAsync(null);
        Check(pickCount == 0 && plain.ProjectPath is null, "Direct menu execution cannot open another folder while Git is busy.");
        plainGit.IsBusy = false;
        await plainGit.OpenFolderCommand.ExecuteAsync(null);
        Check(!plainGit.IsPickerMode && plainGit.SelectedRepository is null && plain.ProjectPath == "/work" && remote.Initializations == 0,
            "An ordinary directory opens for editing without initializing or registering Git.");

        var nameInput = new TaskCompletionSource<string?>();
        plain.Editor.RequestEntryNameAsync = (_, _) => nameInput.Task;
        var create = plain.Editor.PerformTreeActionAsync(plain.Editor.WorkspaceRoots.Single(), EditorTreeAction.NewFile);
        Check(plain.Editor.IsTreeOperationPending && !plain.CanClose && !await plain.PrepareOperationAsync(),
            "A pending file name prevents project close and worktree mutations.");
        await plain.RefreshAsync();
        Check(!plain.Editor.IsOperationLocked, "Project refresh cannot lock the editor while its file-name dialog is open.");
        nameInput.SetResult("created.cs");
        await create;
        Check(store.Files.ContainsKey("/work/created.cs") && plain.Editor.ActiveDocument?.Path == "/work/created.cs"
            && !plain.Editor.IsTreeOperationPending && plain.CanClose,
            "Confirming a pending name creates and opens the file, then releases the project guard.");
        await plain.Editor.CloseDocumentCommand.ExecuteAsync(plain.Editor.ActiveDocument);

        var slowRead = new TaskCompletionSource<byte[]?>();
        store.PendingRead = slowRead;
        var late = plain.Editor.OpenPathAsync("/work/late.cs");
        plain.Dispose();
        slowRead.SetResult(Encoding.UTF8.GetBytes("late"));
        await late;
        Check(plain.Editor.OpenDocuments.Count == 0, "Disposal prevents late file reads from reopening a tab.");
        await EditorContextChecks.RunAsync();
        await GitDockChecks.RunAsync(settings);
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
    public bool FailMaintenanceReads { get; set; }
    public int MaintenanceReads { get; private set; }
    public Dictionary<string, TaskCompletionSource<GitStatusDto>> PendingStatuses { get; } = [];
    public object? LastWorkspaceRequest { get; private set; }
    public IReadOnlyList<GitRemoteDto> Remotes { get; set; } = [];
    public List<GitFetchRequest> FetchRequests { get; } = [];
    public List<GitPullRequest> PullRequests { get; } = [];
    public List<GitCredentialAccountRequest> AccountRequests { get; } = [];
    public List<GitCredentialAccountDto> Accounts { get; } = [];
    public bool RejectAccountSave { get; set; }
    public List<GitCloneRequest> CloneRequests { get; } = [];
    public int Checkouts { get; private set; }
    public int Initializations { get; private set; }
    public GitCommitRequest? LastCommit { get; private set; }
    public TaskCompletionSource<GitDiffDto>? PendingDiff { get; set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case nameof(IRemoteGitClient.ListCredentialAccountsAsync): return Task.FromResult<IReadOnlyList<GitCredentialAccountDto>>(Accounts.ToArray());
            case nameof(IRemoteGitClient.SaveCredentialAccountAsync):
                var account = (GitCredentialAccountRequest)args![0]!; AccountRequests.Add(account);
                if (RejectAccountSave) throw new HttpRequestException("Account save rejected");
                var dto = new GitCredentialAccountDto("account", account.Host, account.Username, account.IsDefault);
                Accounts.Clear(); Accounts.Add(dto); return Task.FromResult(dto);
            case nameof(IRemoteGitClient.DeleteCredentialAccountAsync): Accounts.Clear(); return Task.FromResult(true);
            case nameof(IRemoteGitClient.FetchAsync):
                var fetch = (GitFetchRequest)args![1]!; FetchRequests.Add(fetch);
                return Task.FromResult(new GitOperationResult(fetch.Credentials is not null, "fetch", RequiresCredentials: fetch.Credentials is null,
                    CredentialHost: fetch.Remote == "mirror" ? "gitee.com" : "github.com"));
            case nameof(IRemoteGitClient.PullAsync):
                var pull = (GitPullRequest)args![1]!; PullRequests.Add(pull);
                return Task.FromResult(new GitOperationResult(pull.Credentials is not null, "pull", RequiresCredentials: pull.Credentials is null, CredentialHost: "gitee.com"));
            case nameof(IRemoteGitClient.CreateTagAsync):
            case nameof(IRemoteGitClient.RebaseAsync):
            case nameof(IRemoteGitClient.StashAsync):
            case nameof(IRemoteGitClient.ApplyPatchAsync):
                LastWorkspaceRequest = args![1]; return Task.FromResult(new GitOperationResult(true));
            case nameof(IRemoteGitClient.ExportPatchAsync): return Task.FromResult(new GitPatchDto("exported patch\n"));
            case nameof(IRemoteGitClient.CloneAsync):
                var request = (GitCloneRequest)args![0]!; CloneRequests.Add(request);
                return Task.FromResult(request.Credentials is null
                    ? new GitCloneResult(new(false, "clone", RequiresCredentials: true))
                    : new GitCloneResult(new(true, "clone"), new("cloned", "Cloned", request.Path)));
            case nameof(IRemoteGitClient.GetStatusAsync): return PendingStatuses.TryGetValue((string)args![0]!, out var pendingStatus) ? pendingStatus.Task : Task.FromResult(new GitStatusDto("main", [new("app.cs", Staged: true)], [new("app.cs")], [], [], "v"));
            case nameof(IRemoteGitClient.ListBranchesAsync): return Task.FromResult<IReadOnlyList<GitBranchDto>>([new("main", "abc", IsCurrent: true)]);
            case nameof(IRemoteGitClient.GetGraphAsync): return Task.FromResult<IReadOnlyList<GitGraphCommitDto>>([]);
            case nameof(IRemoteGitClient.GetConsoleAsync): return Task.FromResult<IReadOnlyList<GitConsoleEntryDto>>([]);
            case nameof(IRemoteGitClient.GetLogAsync): return Task.FromResult<IReadOnlyList<GitCommitDto>>([]);
            case nameof(IRemoteGitClient.ListTagsAsync): return Task.FromResult<IReadOnlyList<GitTagDto>>([new("v1-preview", "abc")]);
            case nameof(IRemoteGitClient.ListStashesAsync):
                MaintenanceReads++;
                if (FailMaintenanceReads) throw new HttpRequestException("Not Found", null, System.Net.HttpStatusCode.NotFound);
                return Task.FromResult<IReadOnlyList<GitStashDto>>([]);
            case nameof(IRemoteGitClient.ListWorktreesAsync):
                MaintenanceReads++;
                if (FailMaintenanceReads) throw new HttpRequestException("Not Found", null, System.Net.HttpStatusCode.NotFound);
                return Task.FromResult<IReadOnlyList<GitWorktreeDto>>([]);
            case nameof(IRemoteGitClient.ListRemotesAsync): return Task.FromResult(Remotes);
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
