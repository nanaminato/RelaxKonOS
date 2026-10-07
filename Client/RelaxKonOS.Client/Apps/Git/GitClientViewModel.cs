using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Client.Services.Installation;
using System.Collections.ObjectModel;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Privileged;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.Git;

public enum GitClientPage { Overview, Workspace, Log, Remotes }

/// <summary>A display/value pair used by the compact history filter controls.</summary>
public sealed record GitLogFilterOption(string Value, string Label);

/// <summary>State and typed operations for the Git RelaxKonOS.Client. Uses DispatcherTimer (10s) for status refresh with Interlocked reentrancy guard.
/// Each window owns its own ViewModel instance — supports multiple projects open simultaneously (MultiWindow instance policy).</summary>
public sealed partial class GitClientViewModel(IRemoteGitClient client) : ObservableObject
{
    public InstallationTaskViewModel Installation { get; set; } = null!;

    public ObservableCollection<GitRepositoryDto> Repositories { get; } = [];
    public ObservableCollection<GitBranchDto> Branches { get; } = [];
    public ObservableCollection<GitCommitDto> Commits { get; } = [];
    public ObservableCollection<GitFileChangeDto> StagedFiles { get; } = [];
    public ObservableCollection<GitFileChangeDto> UnstagedFiles { get; } = [];
    public ObservableCollection<GitFileChangeDto> UntrackedFiles { get; } = [];
    public ObservableCollection<GitFileChangeDto> ConflictFiles { get; } = [];
    public ObservableCollection<GitRemoteDto> Remotes { get; } = [];
    public ObservableCollection<GitFileChangeDto> CommitChangedFiles { get; } = [];
    public ObservableCollection<GitLogFilterOption> LogBranchOptions { get; } = [];
    public ObservableCollection<GitLogFilterOption> LogAuthorOptions { get; } = [];
    public ObservableCollection<GitLogFilterOption> LogDateOptions { get; } =
    [
        new("all", "全部时间"),
        new("today", "今天"),
        new("week", "过去 7 天"),
        new("month", "过去 30 天"),
    ];

    /// <summary>Files shown in the Changes list (union of unstaged + untracked, excluding .gitignored).</summary>
    public ObservableCollection<GitFileChangeItem> Changes { get; } = [];

    /// <summary>Tracked files with modifications (already in version control).</summary>
    public ObservableCollection<GitFileChangeItem> TrackedChanges { get; } = [];

    /// <summary>Untracked files (new, not yet in version control).</summary>
    public ObservableCollection<GitFileChangeItem> UntrackedChanges { get; } = [];

    /// <summary>Number of selected files for commit.</summary>
    public int SelectedCount => Changes.Count(c => c.IsSelected);

    /// <summary>Gets the selected file paths for commit.</summary>
    public IReadOnlyList<string> SelectedFilePaths => Changes.Where(c => c.IsSelected).Select(c => c.Path).ToArray();

    // These commands share CanManage.  Notify them when a project is opened from
    // the picker (including a recent/history entry), otherwise their initial
    // disabled state is retained by Avalonia.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PullCommand), nameof(PushCommand), nameof(FetchCommand), nameof(RefreshCommand), nameof(ApplyLogFiltersCommand))]
    private GitRepositoryDto? _selectedRepository;
    [ObservableProperty] private GitClientPage _activePage = GitClientPage.Overview;
    [ObservableProperty] private GitStatusDto? _status;
    [ObservableProperty] private GitBranchDto? _selectedBranch;
    [ObservableProperty] private GitCommitDto? _selectedCommit;
    [ObservableProperty] private GitFileChangeDto? _selectedFile;
    [ObservableProperty] private GitDiffDto? _fileDiff;
    [ObservableProperty] private GitRemoteDto? _selectedRemote;
    [ObservableProperty] private LocalizedStatus _statusText;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PullCommand), nameof(PushCommand), nameof(FetchCommand), nameof(RefreshCommand), nameof(ApplyLogFiltersCommand))]
    private bool _isBusy;
    [ObservableProperty] private bool _isAutoRefresh = true;
    [ObservableProperty] private string _commitMessage = string.Empty;
    [ObservableProperty] private bool _hasConflicts;
    [ObservableProperty] private GitCommitDetailDto? _commitDetail;
    [ObservableProperty] private string _branchSearchText = string.Empty;
    [ObservableProperty] private string _commitSearchText = string.Empty;
    [ObservableProperty] private GitFileChangeDto? _selectedCommitFile;
    [ObservableProperty] private GitLogFilterOption? _selectedLogBranch;
    [ObservableProperty] private GitLogFilterOption? _selectedLogAuthor;
    [ObservableProperty] private GitLogFilterOption? _selectedLogDate;
    [ObservableProperty] private string _logPathFilter = string.Empty;
    [ObservableProperty] private bool _isLogCaseSensitive;
    [ObservableProperty] private bool _isLogRegex;

    // ── Push dialog state ──
    public ObservableCollection<GitCommitDto> PushCommits { get; } = [];
    public ObservableCollection<GitFileChangeDto> PushChangedFiles { get; } = [];
    public ObservableCollection<PushFileTreeNode> PushFileTree { get; } = [];

    [ObservableProperty] private GitCommitDto? _pushSelectedCommit;
    [ObservableProperty] private string _pushSelectedRemote = string.Empty;
    [ObservableProperty] private string _pushSelectedBranch = string.Empty;
    [ObservableProperty] private string _pushLocalBranchName = string.Empty;
    [ObservableProperty] private bool _pushIsLoading;
    [ObservableProperty] private string _pushStatusMessage = string.Empty;

    private int _pushSelectionVersion;

    /// <summary>Whether the preview is focused on a concrete commit rather than the aggregate "all commits" item.</summary>
    public bool PushHasSelectedCommit => PushSelectedCommit is not null;
    public bool PushAllCommitsActive => !PushHasSelectedCommit;
    public string PushBranchLineText => string.IsNullOrWhiteSpace(PushSelectedRemote) || string.IsNullOrWhiteSpace(PushSelectedBranch)
        ? string.Empty
        : LocalizedText.Format("git.dialog.push.branch_line_format", PushLocalBranchName, PushSelectedRemote, PushSelectedBranch);
    public int PushFileCount => PushChangedFiles.Count;
    public int PushCommitCount => PushCommits.Count;
    public bool PushHasCommits => PushCommits.Count > 0;

    // ── 项目选择器状态：IsPickerMode=true 时显示项目选择视图而非工作区 ──
    [ObservableProperty] private bool _isPickerMode = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoadingProjects))]
    private bool _isStarting = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoadingProjects))]
    private bool _isLoadingRepositories;
    [ObservableProperty] private bool _hasLoadedRepositories;
    [ObservableProperty] private string? _repositoryLoadError;
    public bool IsLoadingProjects => IsStarting || IsLoadingRepositories;

    [ObservableProperty] private string _probeHint = string.Empty;
    [ObservableProperty] private bool _isProbing;

    // ── Host Git engine status & install flow (like DockerManagerViewModel) ──
    [ObservableProperty] private bool _isGitAvailable = true;
    [ObservableProperty] private bool _isGitInstallRequired;
    [ObservableProperty] private bool _canAutoInstall;
    [ObservableProperty] private string _engineVersion = "—";
    [ObservableProperty] private string _enginePath = "—";
    [ObservableProperty] private string _problemCode = string.Empty;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private string _installMessage = string.Empty;

    private DispatcherTimer? _timer;
    private DispatcherTimer? _logFilterTimer;
    private int _refreshing;
    private int _logFilterRequestVersion;
    private bool _updatingLogFilterOptions;

    /// <summary>Dialog callbacks assigned by the app shell.</summary>
    public Func<Task<GitCommitRequest?>>? ShowCommitDialogAsync { get; set; }
    /// <summary>Shows the new-branch dialog.  The optional source drives the dialog
    /// title and default name when the action originates from a branch context menu.</summary>
    public Func<GitBranchDto?, Task<GitBranchCreateRequest?>>? ShowCreateBranchDialogAsync { get; set; }
    public Func<Task<GitPullRequest?>>? ShowPullDialogAsync { get; set; }
    /// <summary>Opens the modal resolver when an operation leaves the repository conflicted.</summary>
    public Func<ManagedWindow?, Task>? ShowConflictResolutionDialogAsync { get; set; }
    /// <summary>Offers merge/rebase integration when a push is rejected because the remote advanced.</summary>
    public Func<ManagedWindow?, Task<GitPullRequest?>>? ShowPushRejectedDialogAsync { get; set; }
    public Func<Task<GitRepositoryRegistration?>>? ShowRegisterRepositoryDialogAsync { get; set; }
    public Func<string, Task<bool>>? ShowConfirmAsync { get; set; }
    /// <summary>Assigned by the app shell so operations can surface an unavailable engine immediately.</summary>
    public Func<Task>? ShowGitUnavailableAsync { get; set; }

    /// <summary>Remote folder picker — opens an Explorer-like dialog and returns the selected server-side path, or null on cancel.</summary>
    public Func<Task<string?>>? ShowRemotePathPickerAsync { get; set; }
    /// <summary>Confirms with the user whether to initialize a Git repository at the supplied path.</summary>
    public Func<string, Task<bool>>? ShowInitConfirmAsync { get; set; }
    /// <summary>Prompts the user for new remote name + fetch URL (+ optional push URL).</summary>
    public Func<GitRemoteDto?, Task<GitRemoteRequest?>>? ShowRemoteDialogAsync { get; set; }
    /// <summary>Prompts the user for a new branch name when renaming. Returns null if user cancels.</summary>
    public Func<GitBranchDto, Task<string?>>? ShowRenameBranchDialogAsync { get; set; }
    /// <summary>Prompts the user for merge strategy (merge/no-ff/ff-only/squash) + optional message.
    /// The <paramref name="sourceBranch"/> argument is pre-filled so the dialog can show context.
    /// Returns null if user cancels.</summary>
    public Func<GitBranchDto, Task<GitMergeRequest?>>? ShowMergeDialogAsync { get; set; }
    /// <summary>Prompts the user for an upstream (remote/branch) to track, or choose "Unset" / auto <c>origin/{branch}</c>.
    /// Returns null if user cancels.</summary>
    public Func<GitBranchDto, Task<GitBranchTrackingRequest?>>? ShowSetTrackingDialogAsync { get; set; }
    /// <summary>Displays a modal message box with a single OK button. Used for error/validation
    /// reminders that must grab the user's attention (rather than being silently tucked into StatusText).</summary>
    public Func<string, Task>? ShowMessageAsync { get; set; }
    /// <summary>Provided by the window to surface unavailable privileged operations prominently.</summary>
    public Func<string?, Task>? ShowPrivilegedHelperUnavailableAsync { get; set; }

    /// <summary>Shows the push preview dialog with commit list and file changes.
    /// Returns true if user confirms push, false if cancelled.</summary>
    public Func<Task<bool>>? ShowPushDialogAsync { get; set; }

    /// <summary>Shows a read-only native diff viewer for a single changed file.</summary>
    public Func<ManagedWindow?, GitDiffDto, Task>? ShowFileDiffAsync { get; set; }

    /// <summary>Shows the remote/branch picker dialog for push target selection.
    /// Input: owner window (for correct Z-order), current remote name (may be null), current branch name.
    /// Returns: (remoteName, branchName) tuple or null on cancel.</summary>
    public Func<ManagedWindow?, string?, string?, Task<(string Remote, string Branch)?>>? ShowRemoteBranchPickerDialogAsync { get; set; }

    /// <summary>Shows the credential prompt as a child of the push preview dialog. Returned credentials
    /// are used only by the retry request and are never retained by this view model.</summary>
    public Func<ManagedWindow?, Task<GitCredentialRequest?>>? ShowGitCredentialsDialogAsync { get; set; }

    public bool HasUpstream => Status?.Upstream is not null;
    public bool CanManage => SelectedRepository is not null && !IsBusy;
    public bool CanOpenProject => !IsBusy && IsPickerMode;

    public async Task StartAsync()
    {
        IsStarting = true;
        try
        {
            SelectedLogDate ??= LogDateOptions[0];
            Log("StartAsync 开始");
            await RefreshEngineStatusAsync();
            Log($"引擎状态: IsAvailable={IsGitAvailable} Version={EngineVersion} Problem={ProblemCode}");
            if (!IsGitAvailable)
            {
                IsGitInstallRequired = IsInstallRequired(IsGitAvailable, ProblemCode);
                StatusText = LocalizedText.Ref("git.vm.git_unavailable");
                if (ShowGitUnavailableAsync is not null)
                    await ShowGitUnavailableAsync();
                if (!IsGitAvailable) return; // still unavailable after dialog → stop further init
            }

            await RefreshRepositoriesAsync();
            Log($"项目列表: Repositories.Count={Repositories.Count} IsPickerMode={IsPickerMode}");
            if (RepositoryLoadError is null)
                StatusText = IsPickerMode
                ? (Repositories.Count > 0 ? LocalizedText.Ref("git.status.select_project") : LocalizedText.Ref("git.status.click_open_folder"))
                : LocalizedText.Ref("git.status.ready");

            if (!IsPickerMode && IsAutoRefresh)
                StartStatusTimer();
        }
        finally { IsStarting = false; }
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _logFilterTimer?.Stop();
        _logFilterTimer = null;
    }

    private void StartStatusTimer()
    {
        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += async (_, _) => await RefreshStatusAsync();
        _timer.Start();
    }

    /// <summary>Reloads the registered repository list (project picker source) without leaving picker mode.</summary>
    [RelayCommand]
    public async Task RefreshRepositoriesAsync()
    {
        if (IsLoadingRepositories) return;
        IsLoadingRepositories = true;
        HasLoadedRepositories = false;
        RepositoryLoadError = null;
        Log("RefreshRepositoriesAsync 开始调用 client.ListRepositoriesAsync …");
        try
        {
            var repos = await client.ListRepositoriesAsync();
            Repositories.Clear();
            foreach (var repo in repos) Repositories.Add(repo);
            HasLoadedRepositories = true;
            Log($"RefreshRepositoriesAsync 完成，项目数={Repositories.Count}");
        }
        catch (Exception ex)
        {
            RepositoryLoadError = LocalizedText.Format("git.vm.load_repositories_failed_format", ex.Message);
            StatusText = LocalizedText.Ref("git.vm.load_repositories_failed_format", ex.Message);
            Log($"RefreshRepositoriesAsync 异常：{ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
        }
        finally { IsLoadingRepositories = false; }
    }

    private async Task RefreshAllAsync()
    {
        if (SelectedRepository is null) { Log("RefreshAllAsync: SelectedRepository=null → 跳过"); return; }
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) { Log("RefreshAllAsync: 另一次刷新正在进行 → 跳过"); return; }
        try
        {
            Log("RefreshAllAsync: 并行请求 GetStatus / ListBranches / GetLog(100) …");
            var statusTask = client.GetStatusAsync(SelectedRepository.Id);
            var branchesTask = client.ListBranchesAsync(SelectedRepository.Id);
            var logTask = client.GetLogAsync(SelectedRepository.Id, limit: 100, query: BuildLogQuery());

            Status = await statusTask;
            var branches = await branchesTask;
            var commits = await logTask;
            Log($"收到数据: Status.Branch={Status?.Branch ?? "(null)"} Branches={branches.Count} Commits={commits.Count}");

            Branches.Clear();
            foreach (var b in branches) Branches.Add(b);

            ReplaceCommits(commits);
            RebuildLogBranchOptions();

            StagedFiles.Clear();
            UnstagedFiles.Clear();
            UntrackedFiles.Clear();
            ConflictFiles.Clear();

            if (Status is not null)
            {
                foreach (var f in Status.Staged) StagedFiles.Add(f);
                foreach (var f in Status.Unstaged) UnstagedFiles.Add(f);
                foreach (var f in Status.Untracked) UntrackedFiles.Add(f);
                foreach (var f in Status.Conflicts) ConflictFiles.Add(f);
                HasConflicts = ConflictFiles.Count > 0;
                Log($"文件变更计数: Staged={StagedFiles.Count} Unstaged={UnstagedFiles.Count} " +
                    $"Untracked={UntrackedFiles.Count} Conflicts={ConflictFiles.Count}");
            }
            else
            {
                Log("⚠ Status 返回为 null — 工作区变更与分支信息无法呈现");
            }

            await RefreshConflictStateAsync();
            RebuildChangesList();
            StatusText = LocalizedText.Ref("git.status.ready_branch_format", Status?.Branch ?? LocalizedText.Get("git.status.unknown_branch"));
        }
        catch (Exception ex)
        {
            StatusText = LocalizedText.Ref("git.vm.refresh_failed_format", ex.Message);
            Log($"RefreshAllAsync 异常：{ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (SelectedRepository is null || IsBusy) return;
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        try
        {
            Status = await client.GetStatusAsync(SelectedRepository.Id);
            StagedFiles.Clear();
            UnstagedFiles.Clear();
            UntrackedFiles.Clear();
            ConflictFiles.Clear();
            if (Status is not null)
            {
                foreach (var f in Status.Staged) StagedFiles.Add(f);
                foreach (var f in Status.Unstaged) UnstagedFiles.Add(f);
                foreach (var f in Status.Untracked) UntrackedFiles.Add(f);
                foreach (var f in Status.Conflicts) ConflictFiles.Add(f);
                HasConflicts = ConflictFiles.Count > 0;
            }
            await RefreshConflictStateAsync();
            RebuildChangesList();
        }
        catch { /* silent — timer tick */ }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    [RelayCommand]
    private async Task RefreshEngineStatusAsync()
    {
        try
        {
            var status = await client.GetEngineStatusAsync();
            IsGitAvailable = status.IsAvailable;
            ProblemCode = status.ProblemCode ?? string.Empty;
            EngineVersion = string.IsNullOrWhiteSpace(status.Version) ? "—" : status.Version;
            EnginePath = string.IsNullOrWhiteSpace(status.ExecutablePath) ? "—" : status.ExecutablePath;
            CanAutoInstall = status.CanAutoInstall;
            IsGitInstallRequired = IsInstallRequired(IsGitAvailable, ProblemCode);
            InstallEngineCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            IsGitAvailable = false;
            ProblemCode = "error";
            EngineVersion = "—";
            EnginePath = "—";
            CanAutoInstall = false;
            IsGitInstallRequired = false;
            StatusText = LocalizedText.Ref("git.vm.engine_check_failed_format", ex.Message);
        }
    }

    private bool CanInstallEngine => !IsInstalling && CanAutoInstall && !IsGitAvailable;

    [RelayCommand(CanExecute = nameof(CanInstallEngine))]
    private Task InstallEngineAsync() => Installation.SubmitAsync(InstallationOperationKind.Install, new GitInstallationRequest(true));
    public Task RefreshInstallationAsync() => RefreshEngineStatusAsync();

    private static bool IsInstallRequired(bool isAvailable, string problemCode)
    {
        if (isAvailable) return false;
        return string.Equals(problemCode, "not_installed", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RefreshAsync()
    {
        await RefreshAllAsync();
    }

    /// <summary>Writes [TRACE] logs to the Debug Output window only — does not touch user-facing StatusText.
    /// StatusText should be set via LocalizedText.Get/Format so it remains localized and stable.</summary>
    private void Log(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[TRACE] {message}");
    }

    private async Task LoadCommitDetailAsync(GitCommitDto? commit)
    {
        CommitChangedFiles.Clear();
        CommitDetail = null;
        if (commit is null || SelectedRepository is null) return;
        try
        {
            var detail = await client.GetCommitDetailAsync(SelectedRepository.Id, commit.Sha);
            CommitDetail = detail;
            foreach (var f in detail.ChangedFiles) CommitChangedFiles.Add(f);
        }
        catch (Exception ex)
        {
            // Keep the history list usable if this individual detail request fails.
            StatusText = LocalizedText.Ref("git.vm.load_commit_detail_failed_format", ex.Message);
        }
    }

}
