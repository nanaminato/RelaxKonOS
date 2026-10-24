using Avalonia.Headless;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Git;
using RelaxKonOS.Client.Apps.Git.Views;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Protocol.Desktop;

internal static class GitDockChecks
{
    public static void Run(ShellSettings settings)
    {
        var task = RunAsync(settings); var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("Git dock checks timed out.");
        task.GetAwaiter().GetResult();
    }
    public static async Task RunAsync(ShellSettings settings)
    {
        var api = DispatchProxy.Create<IRemoteGitClient, DockGitClient>();
        var remote = (DockGitClient)(object)api;
        var files = DispatchProxy.Create<IExplorerClient, ProjectFiles>();
        ((ProjectFiles)(object)files).Directories.Add("/dock");
        var vm = new GitClientViewModel(api) { IsStarting = false, HasLoadedRepositories = true };
        using var project = new ProjectEditorViewModel(vm, api, files, true);
        var view = GitClientWorkspace.Create(vm, project);
        var host = new Window { Width = 1475, Height = 670, Content = view };
        host.Show(); Dispatcher.UIThread.RunJobs();
        var repo = new GitRepositoryDto("dock", "Workspace", "/dock");
        vm.Repositories.Add(repo); await vm.OpenProjectCommand.ExecuteAsync(repo);
        view.ToggleGitTool(); Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
        var primary = view.SelectedGitTab!;
        Check(primary.View is GitLogView && primary.Session == vm, "The primary log remains a real log view.");
        var optionsMenu = (MenuFlyout)view.FindControl<Button>("GitDockOptionsButton")!.Flyout!;
        optionsMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "overview")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(view.SelectedGitTab?.View is GitOverviewView, "Management tabs use their distinct view instead of reusing the log content.");
        vm.NavigateToCommand.Execute(GitClientPage.Log);
        Check(view.SelectedGitTab == primary, "Existing page navigation selects the corresponding tab in the new dock.");
        var second = await view.NewLogTabAsync();
        second.Session!.SelectedLogBranch = second.Session.LogBranchOptions.Single(option => option.Value == "feature/ui");
        await second.Session.ApplyLogFiltersCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
        Check((((GitLogView)second.View).FindControl<TreeView>("BranchTree")!.SelectedItem as BranchTreeNode)?.Branch?.Name == "feature/ui", "The branch tree highlights the independent tab's selected branch filter.");
        Check(second.Session.Commits.Select(commit => commit.Sha).SequenceEqual(new[] { DockGitClient.B, DockGitClient.D }) && vm.Commits.Count == DockGitClient.Commits.Length,
            "Two log tabs contain different real query results without sharing collections or filters.");
        var secondView = (GitLogView)second.View;
        secondView.FindControl<CheckBox>("AuthorsOption")!.IsChecked = false;
        var copied = await view.NewLogTabAsync(duplicate: true);
        Dispatcher.UIThread.RunJobs();
        Check(copied.Session != second.Session && copied.Session!.SelectedLogBranch?.Value == "feature/ui" && !((GitLogView)copied.View).ShowAuthors,
            "Duplicating a log copies filters and presentation into a separate session.");
        view.SelectGitTab(primary); Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
        var log = (GitLogView)primary.View;
        Check(log.FindControl<ListBox>("CommitList")!.ContainerFromItem(log.LogRows.First())!.Bounds.Height == 26, "Commit rows are exactly 26 DIP so graph edges connect without gaps.");
        Check(view.FindControl<Button>("ProjectSwitcher")!.Bounds.Width > 70 && view.FindControl<Button>("BranchBadge")!.Bounds.Width > 60,
            "Project and branch header buttons size to their labels instead of inheriting icon-button width.");
        Check(log.LogRows.Count == DockGitClient.Commits.Length && log.LogRows[0].Graph.Edges.Count(edge => !edge.Incoming && !edge.Through && !edge.Cut) == 2
            && log.LogRows.Any(row => row.Graph.Lane > 0), "Graph rows use actual merge parents and distinct branch lanes.");
        Check(log.LogRows.First().References.Any(reference => reference.Name == "main") && log.LogRows.Any(row => row.References.Any(reference => reference.Name == "v1.0")),
            "Branch and tag labels attach to matching commit SHAs.");
        log.FindControl<ListBox>("CommitList")!.SelectedItem = log.LogRows.First();
        Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
        Check(!log.FindControl<Button>("CherryPickButton")!.IsEnabled, "Cherry-pick toolbar is disabled for a commit already reachable from HEAD.");
        Check(vm.SelectedCommit?.Sha == DockGitClient.A && log.CommitFileTreeRoots.Sum(node => node.FileCount) == 4,
            "Selecting a commit loads its own hierarchical changed files and details.");
        var fileNode = Leaves(log.CommitFileTreeRoots).First();
        log.FindControl<TreeView>("ChangedFileTree")!.SelectedItem = fileNode;
        Check(!vm.IsInlineDiffOpen && project.Editor.OpenDocuments.Count == 0, "A single file click selects it without opening an editor or diff.");
        var preview = log.FindControl<Avalonia.Controls.Primitives.ToggleButton>("DiffPreviewToggle")!;
        preview.IsChecked = true; preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Check(vm.IsInlineDiffOpen && vm.FileDiff?.Path == fileNode.File!.Path, "The diff preview toggle opens the selected file's actual patch.");
        vm.CloseInlineDiffCommand.Execute(null); preview.IsChecked = false;
        view.SelectGitTab(second); view.SelectGitTab(primary); Dispatcher.UIThread.RunJobs();
        Check(log.FindControl<TreeView>("ChangedFileTree")!.SelectedItem is CommitFileTreeNode keptFile && keptFile.File?.Path == fileNode.File!.Path,
            "Switching away and back retains the selected changed file.");
        Check(vm.SelectedCommit?.Sha == DockGitClient.A && second.Session.SelectedLogBranch?.Value == "feature/ui" && log.ShowAuthors,
            "Switching tabs preserves commit selection, filters and presentation independently.");
        var history = await view.NewLogTabAsync(path: "Client/Views/Workspace.axaml");
        Check(history.Kind == "history" && history.Session!.LogPathFilter == "Client/Views/Workspace.axaml" && history.Session.Commits.Count == 2,
            "A history tab queries its own path and has different content from log and console tabs.");
        remote.PendingCheckout = new();
        var checkout = second.Session.CheckoutBranchCommand.ExecuteAsync(second.Session.Branches.Single(branch => branch.Name == "feature/ui"));
        Check(vm.IsBusy && project.Editor.IsReadOnly && copied.Session!.IsBusy, "A mutation in a secondary log locks the shared repository and editor.");
        view.CloseGitTab(second); Check(view.GitTabs.Contains(second), "A running Git operation cannot be abandoned by closing its log.");
        remote.PendingCheckout.SetResult(new(true)); await checkout; remote.PendingCheckout = null;
        Dispatcher.UIThread.RunJobs();
        Check(!vm.IsBusy && !second.Session.IsBusy, "Completing a secondary log operation releases the shared busy state.");
        view.SelectGitTab(copied);
        remote.PendingLog = new(); copied.Session!.CommitSearchText = "slow";
        var delayed = copied.Session.ApplyLogFiltersCommand.ExecuteAsync(null);
        view.CloseGitTab(copied); var selectedAfterClose = view.SelectedGitTab;
        remote.PendingLog.SetResult([DockGitClient.Commits[0]]); await delayed; remote.PendingLog = null;
        Check(!view.GitTabs.Contains(copied) && view.SelectedGitTab == selectedAfterClose, "A late response cannot reopen a closed log or switch the active tab.");
        remote.ConsoleEntries.Add(new(1, DateTimeOffset.UtcNow, "git checkout feature/ui", "Switched to branch 'feature/ui'\n", "", 0));
        await view.RefreshGitConsoleAsync();
        var console = view.GitTabs.Single(tab => tab.Kind == "console");
        Check(console.View is GitConsoleView output && output.Text.Contains("git checkout feature/ui") && view.SelectedGitTab == selectedAfterClose,
            "Real transcript records create a separate console without stealing the selected log.");
        view.SelectGitTab(console); Dispatcher.UIThread.RunJobs();
        var clear = console.View.GetVisualDescendants().OfType<Button>().Single(button => Equals(ToolTip.GetTip(button), LocalizedText.Get("git.dock.clear_console")));
        clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await view.RefreshGitConsoleAsync();
        Check(((GitConsoleView)console.View).Text.Length == 0, "Clearing the console does not replay already consumed records.");
        remote.PendingConsole = new(); var pendingConsole = view.RefreshGitConsoleAsync(); view.CloseGitTab(console);
        remote.PendingConsole.SetResult([new(2, DateTimeOffset.UtcNow, "git fetch origin", "Fetched\n", "", 0)]); await pendingConsole; remote.PendingConsole = null;
        Check(view.GitTabs.All(tab => tab.Kind != "console"), "Closing the console discards an in-flight transcript without reopening it.");
        view.SelectGitTab(primary); Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
        var max = view.FindControl<Button>("MaximizeGitButton")!;
        max.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
        Check(!view.FindControl<Grid>("MainWorkspace")!.IsVisible && view.FindControl<Border>("GitToolHost")!.Bounds.Height > 600,
            "The Git dock maximizes to the available workspace height.");
        view.ToggleGitTool(); view.ToggleGitTool(); Dispatcher.UIThread.RunJobs();
        Check(view.SelectedGitTab == primary && vm.SelectedCommit?.Sha == DockGitClient.A, "Hiding and restoring the dock preserves tabs and selection.");
        var outputDir = Path.Combine(AppContext.BaseDirectory, "preview-qa", "git-dock"); Directory.CreateDirectory(outputDir);
        var oldLanguage = settings.Language; var oldAppearance = settings.Appearance;
        try
        {
            foreach (var language in new[] { "zh-CN", "en-US", "ja-JP" }) foreach (var dark in new[] { false, true })
            {
                settings.Language = language; settings.Appearance = settings.Appearance with { Mode = dark ? ThemeKind.Dark : ThemeKind.Light };
                project.RefreshLocalization();
                remote.ConsoleEntries.Add(new(remote.ConsoleEntries.Count + 10, DateTimeOffset.UtcNow, "git fetch origin", "From /server/remote\nAlready up to date.\n", "", 0));
                await view.RefreshGitConsoleAsync();
                view.SelectGitTab(primary); Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
                using var bitmap = new RenderTargetBitmap(new PixelSize(1475, 670)); bitmap.Render(host);
                bitmap.Save(Path.Combine(outputDir, $"git-log-{language}-{(dark ? "dark" : "light")}.png"), new PngBitmapEncoderOptions());
                var dock = view.FindControl<Border>("GitToolHost")!;
                using var dockBitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(dock.Bounds.Width), (int)Math.Ceiling(dock.Bounds.Height)));
                dockBitmap.Render(dock);
                dockBitmap.Save(Path.Combine(outputDir, $"git-toolwindow-{language}-{(dark ? "dark" : "light")}.png"), new PngBitmapEncoderOptions());
                var consoleTab = view.GitTabs.Single(tab => tab.Kind == "console"); view.SelectGitTab(consoleTab); Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
                using var consoleBitmap = new RenderTargetBitmap(new PixelSize(1475, 670)); consoleBitmap.Render(host);
                consoleBitmap.Save(Path.Combine(outputDir, $"git-console-{language}-{(dark ? "dark" : "light")}.png"), new PngBitmapEncoderOptions());
            }
        }
        finally { settings.Language = oldLanguage; settings.Appearance = oldAppearance; project.RefreshLocalization(); }
        view.SelectGitTab(primary); max.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
        Check(view.FindControl<Grid>("MainWorkspace")!.IsVisible, "Restoring Git dock size returns to the editor.");
        foreach (var width in new[] { 860, 640, 320 })
        {
            host.Width = width; Dispatcher.UIThread.RunJobs(); host.UpdateLayout(); await Task.Delay(60); Dispatcher.UIThread.RunJobs(); using var frame = host.CaptureRenderedFrame();
            using (var narrow = new RenderTargetBitmap(new PixelSize(width, 670))) { narrow.Render(host); narrow.Save(Path.Combine(outputDir, $"narrow-{width}.png")); }
            foreach (var button in new[] { view.FindControl<Button>("NewGitLogButton")!, view.FindControl<Button>("MaximizeGitButton")!, view.FindControl<Button>("HideGitButton")! })
            {
                var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), host)!.Value;
                Check(point.X >= 0 && point.X < width && host.InputHitTest(point) is Visual hit && (hit == button || hit.GetVisualAncestors().Contains(button)),
                    $"Git dock controls remain reachable at {width} DIP.");
            }
        }
        view.CloseGitTab(second);
        Check(!view.GitTabs.Contains(second), "An idle secondary log can be closed independently.");
        max.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        project.Editor.NewDocumentCommand.Execute(null); Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
        Check(view.FindControl<Grid>("MainWorkspace")!.IsVisible, "Opening an editor document restores it from a maximized Git dock.");
        host.Close(); Console.WriteLine("Git dock multi-tab behavior and populated previews passed.");
    }
    private static IEnumerable<CommitFileTreeNode> Leaves(IEnumerable<CommitFileTreeNode> nodes)
        => nodes.SelectMany(node => node.IsFile ? new[] { node } : Leaves(node.Children));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS GIT DOCK: " + message); }
}

public class DockGitClient : DispatchProxy
{
    public const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", C = "cccccccccccccccccccccccccccccccccccccccc", D = "dddddddddddddddddddddddddddddddddddddddd";
    private static readonly GitCommitDto[] InitialCommits = [new(A, "aaaaaaa", "nanaminato", "dev@example.invalid", "2026-10-24T12:13:00+09:00", "Merge feature/ui into main", "Merge the independent Git log tabs.\nPreserve each tab's filters and selection."), new(B, "bbbbbbb", "nanaminato", "dev@example.invalid", "2026-10-24T11:35:00+09:00", "Add independent log, history and console tabs"), new(C, "ccccccc", "nanami", "dev@example.invalid", "2026-10-24T11:27:00+09:00", "Refine the file tree and commit details"), new(D, "ddddddd", "nanami", "dev@example.invalid", "2026-10-23T23:57:00+09:00", "Initial workspace")];
    private static string ExtraSha(int index) => (index + 100).ToString("x40");
    public static readonly GitCommitDto[] Commits = InitialCommits.Concat(Enumerable.Range(0, 20).Select(index => new GitCommitDto(ExtraSha(index), ExtraSha(index)[..7], index % 3 == 0 ? "nanami" : "nanaminato", "dev@example.invalid", $"2026-10-{23 - index / 8:00}T{23 - index % 8:00}:15:00+09:00", new[] { "Refine the project file tree and context actions", "Add localization for editor commands", "Keep unsaved document buffers during checkout", "Render Git history with parent connections", "Update workspace documentation" }[index % 5]))).ToArray();
    public TaskCompletionSource<IReadOnlyList<GitCommitDto>>? PendingLog;
    public TaskCompletionSource<GitOperationResult>? PendingCheckout;
    public TaskCompletionSource<IReadOnlyList<GitConsoleEntryDto>>? PendingConsole;
    public List<GitConsoleEntryDto> ConsoleEntries { get; } = [];
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case nameof(IRemoteGitClient.GetStatusAsync): return Task.FromResult(new GitStatusDto("main", [], [], [], [], "v"));
            case nameof(IRemoteGitClient.ListBranchesAsync): return Task.FromResult<IReadOnlyList<GitBranchDto>>([new("main", A, IsCurrent: true), new("feature/ui", B), new("origin/main", A, IsRemote: true)]);
            case nameof(IRemoteGitClient.GetGraphAsync): return Task.FromResult<IReadOnlyList<GitGraphCommitDto>>(new GitGraphCommitDto[] { new(A, [B, C], true), new(B, [D], false), new(C, [D], false), new(D, [ExtraSha(0)], false) }.Concat(Enumerable.Range(0, 20).Select(index => new GitGraphCommitDto(ExtraSha(index), index == 19 ? [] : [ExtraSha(index + 1)], false))).ToArray());
            case nameof(IRemoteGitClient.ListTagsAsync): return Task.FromResult<IReadOnlyList<GitTagDto>>([new("v1.0", D)]);
            case nameof(IRemoteGitClient.ListRemotesAsync): return Task.FromResult<IReadOnlyList<GitRemoteDto>>([]);
            case nameof(IRemoteGitClient.GetConflictStateAsync): return Task.FromResult(new GitConflictStateDto(null, []));
            case nameof(IRemoteGitClient.GetConsoleAsync): return PendingConsole?.Task ?? Task.FromResult<IReadOnlyList<GitConsoleEntryDto>>(ConsoleEntries.Where(entry => entry.Sequence > (long)args![1]!).ToArray());
            case nameof(IRemoteGitClient.GetLogAsync):
                var query = args![3] as GitLogQuery;
                if (query?.Search == "slow" && PendingLog is not null) return PendingLog.Task;
                return Task.FromResult<IReadOnlyList<GitCommitDto>>(query?.Path is { Length: > 0 } ? [Commits[2], Commits[3]] : query?.Reference == "feature/ui" ? [Commits[1], Commits[3]] : Commits);
            case nameof(IRemoteGitClient.GetCommitDetailAsync):
                var commit = Commits.Single(commit => commit.Sha == (string)args![1]!);
                return Task.FromResult(new GitCommitDetailDto(commit.Sha, commit.Author, commit.AuthorDate, commit.Subject, [B, C], [new("Client/Views/Workspace.axaml"), new("Client/Views/Workspace.axaml.cs"), new("Client/ViewModels/GitLog.cs"), new("docs/Git.md")], commit.Body));
            case nameof(IRemoteGitClient.GetDiffAsync): return Task.FromResult(new GitDiffDto((string)args![1]!, "v", Patch: "diff --git a/file b/file\n--- a/file\n+++ b/file\n@@ -1 +1 @@\n-old\n+new\n"));
            case nameof(IRemoteGitClient.CheckoutAsync): return PendingCheckout?.Task ?? Task.FromResult(new GitOperationResult(true));
            default: throw new NotSupportedException(method.Name);
        }
    }
}
