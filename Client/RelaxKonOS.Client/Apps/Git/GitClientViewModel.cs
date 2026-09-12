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
    [ObservableProperty] private string _statusText = LocalizedText.Get("git.status.loading");
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
                StatusText = LocalizedText.Get("git.vm.git_unavailable");
                if (ShowGitUnavailableAsync is not null)
                    await ShowGitUnavailableAsync();
                if (!IsGitAvailable) return; // still unavailable after dialog → stop further init
            }

            await RefreshRepositoriesAsync();
            Log($"项目列表: Repositories.Count={Repositories.Count} IsPickerMode={IsPickerMode}");
            if (RepositoryLoadError is null)
                StatusText = IsPickerMode
                ? (Repositories.Count > 0 ? LocalizedText.Get("git.status.select_project") : LocalizedText.Get("git.status.click_open_folder"))
                : LocalizedText.Get("git.status.ready");

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
            StatusText = RepositoryLoadError;
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
            StatusText = LocalizedText.Format("git.status.ready_branch_format", Status?.Branch ?? LocalizedText.Get("git.status.unknown_branch"));
        }
        catch (Exception ex)
        {
            StatusText = LocalizedText.Format("git.vm.refresh_failed_format", ex.Message);
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
            StatusText = LocalizedText.Format("git.vm.engine_check_failed_format", ex.Message);
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

    /// <summary>Runs the history query currently shown in the toolbar.  This is also
    /// used after a branch/user/date selection so every visible result comes from
    /// Git, rather than an arbitrary client-side page of 100 commits.</summary>
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task ApplyLogFiltersAsync()
    {
        if (SelectedRepository is null) return;
        _logFilterTimer?.Stop();
        var requestVersion = Interlocked.Increment(ref _logFilterRequestVersion);
        try
        {
            var commits = await client.GetLogAsync(SelectedRepository.Id, limit: 100, query: BuildLogQuery());
            if (requestVersion != Volatile.Read(ref _logFilterRequestVersion)) return;
            ReplaceCommits(commits);
        }
        catch (Exception ex)
        {
            if (requestVersion == Volatile.Read(ref _logFilterRequestVersion))
                StatusText = LocalizedText.Format("git.vm.refresh_failed_format", ex.Message);
        }
    }

    private GitLogQuery BuildLogQuery() => new(
        Reference: SelectedLogBranch?.Value,
        Search: string.IsNullOrWhiteSpace(CommitSearchText) ? null : CommitSearchText.Trim(),
        Author: SelectedLogAuthor?.Value,
        Path: string.IsNullOrWhiteSpace(LogPathFilter) ? null : LogPathFilter.Trim(),
        DateRange: SelectedLogDate?.Value,
        CaseSensitive: IsLogCaseSensitive,
        UseRegex: IsLogRegex);

    private void ReplaceCommits(IReadOnlyList<GitCommitDto> commits)
    {
        var selectedSha = SelectedCommit?.Sha;
        Commits.Clear();
        foreach (var commit in commits) Commits.Add(commit);
        SelectedCommit = string.IsNullOrEmpty(selectedSha)
            ? null
            : Commits.FirstOrDefault(commit => string.Equals(commit.Sha, selectedSha, StringComparison.Ordinal));
        RebuildLogAuthorOptions();
    }

    private void RebuildLogBranchOptions()
    {
        var selectedValue = SelectedLogBranch?.Value;
        _updatingLogFilterOptions = true;
        try
        {
            LogBranchOptions.Clear();
            LogBranchOptions.Add(new GitLogFilterOption(string.Empty, "分支: HEAD"));
            foreach (var branch in Branches.OrderBy(branch => branch.IsRemote).ThenBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase))
                LogBranchOptions.Add(new GitLogFilterOption(branch.Name, branch.Name));
            SelectedLogBranch = LogBranchOptions.FirstOrDefault(option => option.Value == selectedValue)
                ?? LogBranchOptions[0];
        }
        finally { _updatingLogFilterOptions = false; }
    }

    private void RebuildLogAuthorOptions()
    {
        var selectedValue = SelectedLogAuthor?.Value;
        var knownAuthors = LogAuthorOptions.Select(option => option.Value)
            .Concat(Commits.Select(commit => commit.Author))
            .Where(author => !string.IsNullOrWhiteSpace(author))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(author => author, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _updatingLogFilterOptions = true;
        try
        {
            LogAuthorOptions.Clear();
            LogAuthorOptions.Add(new GitLogFilterOption(string.Empty, "用户: 全部"));
            foreach (var author in knownAuthors)
                LogAuthorOptions.Add(new GitLogFilterOption(author, author));
            SelectedLogAuthor = LogAuthorOptions.FirstOrDefault(option => option.Value == selectedValue)
                ?? LogAuthorOptions[0];
        }
        finally { _updatingLogFilterOptions = false; }
    }

    partial void OnSelectedLogBranchChanged(GitLogFilterOption? value)
    {
        if (!_updatingLogFilterOptions && value is not null && SelectedRepository is not null)
            _ = ApplyLogFiltersAsync();
    }

    partial void OnSelectedLogAuthorChanged(GitLogFilterOption? value)
    {
        if (!_updatingLogFilterOptions && value is not null && SelectedRepository is not null)
            _ = ApplyLogFiltersAsync();
    }

    partial void OnSelectedLogDateChanged(GitLogFilterOption? value)
    {
        if (value is not null && SelectedRepository is not null)
            _ = ApplyLogFiltersAsync();
    }

    partial void OnCommitSearchTextChanged(string value) => QueueLogFilterRefresh();
    partial void OnLogPathFilterChanged(string value) => QueueLogFilterRefresh();
    partial void OnIsLogCaseSensitiveChanged(bool value) => QueueLogFilterRefresh();
    partial void OnIsLogRegexChanged(bool value) => QueueLogFilterRefresh();

    private void QueueLogFilterRefresh()
    {
        if (SelectedRepository is null) return;
        _logFilterTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _logFilterTimer.Tick -= OnLogFilterTimerTick;
        _logFilterTimer.Tick += OnLogFilterTimerTick;
        _logFilterTimer.Stop();
        _logFilterTimer.Start();
    }

    private void OnLogFilterTimerTick(object? sender, EventArgs e)
    {
        _logFilterTimer?.Stop();
        _ = ApplyLogFiltersAsync();
    }

    /// <summary>在项目选择器中点击已注册的项目，进入工作区。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenProject))]
    private async Task OpenProjectAsync(GitRepositoryDto repo)
    {
        Log($"OpenProjectAsync 开始: name={repo?.Name} id={repo?.Id}");
        if (repo is null) { Log("repo 为 null → 退出"); return; }
        try
        {
            Log($"切换 IsPickerMode=false  SelectedRepository={repo.Name}");
            IsPickerMode = false;
            SelectedRepository = repo;
            ActivePage = GitClientPage.Overview;
            StatusText = LocalizedText.Format("git.vm.loading_project_format", repo.Name);

            Log("调用 RefreshAllAsync（并行 status/branches/log）…");
            await RefreshAllAsync();
            await PresentConflictResolutionAsync();
            Log($"RefreshAllAsync 完成。Branches={Branches.Count} Commits={Commits.Count} " +
                $"Staged={StagedFiles.Count} Unstaged={UnstagedFiles.Count} Untracked={UntrackedFiles.Count}");

            Log("启动自动刷新（10s 轮询）…");
            StartStatusTimer();

            Log("调用 RefreshRemotesAsync…");
            await RefreshRemotesAsync();
            Log($"OpenProjectAsync 结束，Remotes={Remotes.Count}，Ready");
            StatusText = LocalizedText.Format("git.vm.ready_project_branch_format", repo.Name, Status?.Branch ?? LocalizedText.Get("git.status.unknown_branch"));
        }
        catch (Exception ex)
        {
            await NotifyAsync(LocalizedText.Format("git.vm.open_project_failed_format", ex.Message));
            Log($"OpenProjectAsync 异常：{ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>打开远程文件夹选择器；选中后探测 Git 状态：是仓库则注册并打开；否则提示初始化。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenProject))]
    private async Task OpenFolderAsync()
    {
        Log("OpenFolderAsync 开始");
        if (ShowRemotePathPickerAsync is null)
        {
            await NotifyAsync(LocalizedText.Get("git.vm.path_picker_unavailable"));
            Log("ShowRemotePathPickerAsync 委托未设置");
            return;
        }

        var path = await ShowRemotePathPickerAsync();
        Log($"路径选择返回: {(path is null ? "null" : $"\"{path}\"")}");
        if (string.IsNullOrWhiteSpace(path)) return;

        await ProbeAndOpenAsync(path);
    }

    /// <summary>手动注册一个绝对路径作为 Git 项目（输入对话框形式）。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenProject))]
    private async Task RegisterRepositoryAsync()
    {
        Log("RegisterRepositoryAsync 开始");
        if (ShowRegisterRepositoryDialogAsync is null) { Log("ShowRegisterRepositoryDialogAsync=null"); return; }
        var registration = await ShowRegisterRepositoryDialogAsync();
        Log($"对话框返回: {(registration is null ? "null" : $"{registration.Name} @ {registration.Path}")}");
        if (registration is null) return;
        try
        {
            var dto = await client.RegisterRepositoryAsync(registration);
            if (!Repositories.Contains(dto))
                Repositories.Add(dto);
            StatusText = LocalizedText.Format("git.vm.registered_format", dto.Name);
            await OpenProjectCommand.ExecuteAsync(dto);
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.register_failed_format", ex.Message)); Log(ex.ToString()); }
    }

    private async Task ProbeAndOpenAsync(string path)
    {
        Log($"ProbeAndOpenAsync path={path}");
        IsProbing = true;
        IsBusy = true;
        ProbeHint = LocalizedText.Format("git.picker.probing_format", path);
        try
        {
            Log("调用 client.ProbeRepositoryAsync …");
            var probe = await client.ProbeRepositoryAsync(path);
            Log($"Probe 返回: IsRepository={probe.IsRepository} HasCommits={probe.HasCommits} Branch={probe.CurrentBranch} Remotes={probe.Remotes?.Count ?? 0}");
            if (!probe.IsRepository)
            {
                ProbeHint = LocalizedText.Get("git.vm.not_repo");
                var init = ShowInitConfirmAsync is not null && await ShowInitConfirmAsync(path);
                Log($"ShowInitConfirm 返回: {init}");
                if (!init)
                {
                    StatusText = LocalizedText.Get("git.vm.init_cancelled");
                    return;
                }
                var initResult = await client.InitRepositoryAsync(path);
                Log($"git init 返回: Success={initResult.Success} Message={initResult.Message}");
                if (!initResult.Success)
                {
                    await NotifyAsync(LocalizedText.Format("git.vm.init_failed_format", initResult.Message));
                    return;
                }
                StatusText = LocalizedText.Get("git.vm.initialized");
                probe = await client.ProbeRepositoryAsync(path);
                Log($"重探测后: IsRepository={probe.IsRepository} DefaultBranch={probe.DefaultBranch}");
            }

            // 已是 Git 仓库：检查是否已注册
            var existing = Repositories.FirstOrDefault(r =>
                string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                Log($"路径已注册为「{existing.Name}」，直接打开");
                StatusText = LocalizedText.Format("git.vm.project_exists_format", existing.Name);
                await OpenProjectCommand.ExecuteAsync(existing);
                return;
            }

            // 未注册：自动注册（名称取路径末段）
            var name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name)) name = path;
            Log($"自动注册: name={name} path={path}");
            var dto = await client.RegisterRepositoryAsync(new GitRepositoryRegistration(name, path));
            if (!Repositories.Contains(dto))
                Repositories.Add(dto);
            StatusText = LocalizedText.Format("git.vm.project_registered_format", dto.Name);
            await OpenProjectCommand.ExecuteAsync(dto);
        }
        catch (Exception ex)
        {
            await NotifyAsync(LocalizedText.Format("git.vm.probe_failed_format", ex.Message));
            ProbeHint = LocalizedText.Format("git.vm.probe_failed_format", ex.Message);
            Log($"ProbeAndOpenAsync 异常：{ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            IsProbing = false;
            IsBusy = false;
        }
    }

    /// <summary>Writes [TRACE] logs to the Debug Output window only — does not touch user-facing StatusText.
    /// StatusText should be set via LocalizedText.Get/Format so it remains localized and stable.</summary>
    private void Log(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[TRACE] {message}");
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CheckoutAsync(GitBranchDto branch)
    {
        if (SelectedRepository is null || branch is null) return;
        IsBusy = true;
        StatusText = LocalizedText.Format("git.vm.checkout_progress_format", branch.Name);
        try
        {
            var result = await client.CheckoutAsync(SelectedRepository.Id, new GitCheckoutRequest(branch.Name));
            if (result.Success)
            {
                StatusText = LocalizedText.Format("git.vm.switched_to_format", branch.Name);
                await RefreshAllAsync();
            }
            else
            {
                await NotifyAsync(LocalizedText.Format("git.vm.checkout_failed_format", result.Message));
                if (result.Conflicts is not null && result.Conflicts.Count > 0)
                {
                    await RefreshAllAsync();
                    await PresentConflictResolutionAsync();
                }
            }
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CreateBranchAsync()
    {
        if (ShowCreateBranchDialogAsync is null || SelectedRepository is null) return;
        var request = await ShowCreateBranchDialogAsync(null);
        if (request is null) return;
        IsBusy = true;
        try
        {
            var result = await client.CreateBranchAsync(SelectedRepository.Id, request);
            if (result.Success)
                StatusText = LocalizedText.Format("git.vm.branch_created_format", request.Name);
            else
                await NotifyAsync(LocalizedText.Format("git.vm.failed_format", result.Message));
            await RefreshAllAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task DeleteBranchAsync(GitBranchDto branch)
    {
        if (SelectedRepository is null || branch is null || branch.IsCurrent) return;
        if (ShowConfirmAsync is not null && !await ShowConfirmAsync(LocalizedText.Format("git.vm.delete_confirm_format", branch.Name)))
            return;
        IsBusy = true;
        try
        {
            var result = await client.DeleteBranchAsync(SelectedRepository.Id, branch.Name);
            if (result.Success)
                StatusText = LocalizedText.Format("git.vm.branch_deleted_format", branch.Name);
            else
                await NotifyAsync(LocalizedText.Format("git.vm.failed_format", result.Message));
            await RefreshAllAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CommitAsync()
    {
        if (SelectedRepository is null) return;

        // 工作区已勾选文件且输入了提交消息 → 直接提交，不再弹对话框
        if (SelectedCount > 0 && !string.IsNullOrWhiteSpace(CommitMessage))
        {
            await CommitDirectAsync(amend: false);
            return;
        }

        // 兜底：工作区没输入消息或没勾选文件时，回退到对话框
        if (ShowCommitDialogAsync is not null)
        {
            var request = await ShowCommitDialogAsync();
            if (request is null) return;
            IsBusy = true;
            try
            {
                var result = await client.CommitAsync(SelectedRepository.Id, request);
                if (result.Success)
                {
                    StatusText = LocalizedText.Get("git.status.committed");
                    CommitMessage = string.Empty;
                    await RefreshAllAsync();
                    await ShowPushDialogAfterCommitAsync();
                }
                else
                {
                    await NotifyAsync(LocalizedText.Format("git.status.commit_failed_format", result.Message));
                }
            }
            catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
            finally { IsBusy = false; }
        }
        else
        {
            if (SelectedCount == 0)
                await NotifyAsync(LocalizedText.Get("git.status.no_files_selected"));
            else if (string.IsNullOrWhiteSpace(CommitMessage))
                await NotifyAsync(LocalizedText.Get("git.status.commit_message_required"));
        }
    }

    /// <summary>使用工作区已勾选的文件和已输入的提交消息直接发起提交，不经过对话框。
    /// 仅在已具备这两个前置条件时调用，调用方需自行校验。</summary>
    private async Task CommitDirectAsync(bool amend)
    {
        if (SelectedRepository is null) return;
        if (SelectedCount == 0)
        {
            await NotifyAsync(LocalizedText.Get("git.status.no_files_selected"));
            return;
        }
        if (string.IsNullOrWhiteSpace(CommitMessage))
        {
            await NotifyAsync(LocalizedText.Get("git.status.commit_message_required"));
            return;
        }
        IsBusy = true;
        try
        {
            var request = new GitCommitRequest(CommitMessage, SelectedFilePaths.ToArray(), amend);
            var result = await client.CommitAsync(SelectedRepository.Id, request);
            if (result.Success)
            {
                StatusText = LocalizedText.Get("git.status.committed");
                CommitMessage = string.Empty;
                await RefreshAllAsync();
                await ShowPushDialogAfterCommitAsync();
            }
            else
            {
                await NotifyAsync(LocalizedText.Format("git.status.commit_failed_format", result.Message));
            }
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    /// <summary>弹出单按钮模态消息框。优先使用注入的 <see cref="ShowMessageAsync"/>，
    /// 未注入时降级为写入 StatusText，保证逻辑链路不被打断。</summary>
    private async Task NotifyAsync(string message)
    {
        StatusText = message; // 同步写入状态栏，便于对话框关闭后用户仍能查阅
        if (ShowMessageAsync is not null)
            await ShowMessageAsync(message);
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task PullAsync()
    {
        if (SelectedRepository is null) return;
        GitPullRequest? request = new();
        if (ShowPullDialogAsync is not null)
            request = await ShowPullDialogAsync();
        if (request is null) return;
        IsBusy = true;
        StatusText = LocalizedText.Get("git.vm.pull_progress");
        try
        {
            var result = await client.PullAsync(SelectedRepository.Id, request);
            if (result.Conflicts is { Count: > 0 })
            {
                await RefreshAllAsync();
                StatusText = LocalizedText.Format("git.vm.merge_conflicts_format", result.Conflicts.Count);
                await PresentConflictResolutionAsync();
            }
            else if (result.RequiresCredentials)
                await NotifyAsync(LocalizedText.Get("git.vm.credentials_required"));
            else if (result.Success)
            {
                await RefreshAllAsync();
                StatusText = LocalizedText.Get("git.vm.pulled");
            }
            else
                await NotifyAsync(LocalizedText.Format("git.vm.pull_failed_format", result.Message));
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task PushAsync()
    {
        if (SelectedRepository is null) return;
        IsBusy = true;
        try
        {
            await PreparePushPreviewAsync();

            if (ShowPushDialogAsync is not null)
            {
                await ShowPushDialogAsync();
            }
            else
            {
                await ExecutePushFromDialogAsync(null);
            }
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task FetchAsync()
    {
        if (SelectedRepository is null) return;
        IsBusy = true;
        try
        {
            var result = await client.FetchAsync(SelectedRepository.Id);
            if (result.Success)
                StatusText = LocalizedText.Get("git.vm.fetched");
            else if (result.RequiresCredentials)
                await NotifyAsync(LocalizedText.Get("git.vm.credentials_required_short"));
            else
                await NotifyAsync(LocalizedText.Format("git.vm.failed_format", result.Message));
            await RefreshAllAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ViewDiffAsync(GitFileChangeDto file)
    {
        if (SelectedRepository is null || file is null) return;
        try
        {
            FileDiff = await client.GetDiffAsync(SelectedRepository.Id, file.Path, file.Staged);
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.diff_failed_format", ex.Message)); }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RevertAsync(GitCommitDto commit)
    {
        if (SelectedRepository is null || commit is null) return;
        if (ShowConfirmAsync is not null && !await ShowConfirmAsync(LocalizedText.Format("git.vm.revert_confirm_format", commit.ShortSha)))
            return;
        IsBusy = true;
        try
        {
            var result = await client.RevertAsync(SelectedRepository.Id, new GitRevertRequest(commit.Sha));
            if (result.Success)
                StatusText = LocalizedText.Get("git.vm.reverted");
            if (result.Conflicts is not null && result.Conflicts.Count > 0)
            {
                await RefreshAllAsync();
                await PresentConflictResolutionAsync();
            }
            else if (!result.Success)
                await NotifyAsync(LocalizedText.Format("git.vm.revert_failed_format", result.Message));
            await RefreshAllAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task StageFileAsync(GitFileChangeDto file)
    {
        if (SelectedRepository is null || file is null) return;
        IsBusy = true;
        try
        {
            var result = await client.StageAsync(SelectedRepository.Id, new GitStageRequest([file.Path]));
            if (result.Success)
                StatusText = LocalizedText.Format("git.vm.staged_format", file.Path);
            else
                await NotifyAsync(LocalizedText.Format("git.vm.stage_failed_format", result.Message));
            await RefreshAllAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.stage_failed_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    // ── 工作区变更选择管理 ──

    /// <summary>Checks if a file path is selected for commit.</summary>
    public bool IsFileSelected(string path) => Changes.Any(c => c.Path == path && c.IsSelected);

    /// <summary>Toggles file selection for commit.</summary>
    public void ToggleFileSelection(GitFileChangeItem item)
    {
        item.IsSelected = !item.IsSelected;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedFilePaths));
    }

    /// <summary>Sets file selection state.</summary>
    public void SetFileSelection(GitFileChangeItem item, bool isSelected)
    {
        item.IsSelected = isSelected;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedFilePaths));
    }

    /// <summary>Selects all files in the Changes list.</summary>
    [RelayCommand]
    private void SelectAllChanges()
    {
        foreach (var c in Changes) c.IsSelected = true;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedFilePaths));
    }

    /// <summary>Clears all selected files.</summary>
    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var c in Changes) c.IsSelected = false;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedFilePaths));
    }

    /// <summary>Refreshes changes list (re-processes .gitignore rules by re-fetching status).</summary>
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RefreshChangesAsync()
    {
        await RefreshStatusAsync();
        StatusText = LocalizedText.Format("git.status.refreshed_changes", Changes.Count);
    }

    /// <summary>Rebuilds the Changes list from UnstagedFiles + UntrackedFiles, preserving selection state.</summary>
    private void RebuildChangesList()
    {
        // Save currently selected paths before rebuilding
        var selectedPaths = new HashSet<string>(Changes.Where(c => c.IsSelected).Select(c => c.Path));

        // Unsubscribe old items
        foreach (var item in Changes)
            item.SelectionChanged -= OnItemSelectionChanged;

        Changes.Clear();
        TrackedChanges.Clear();
        UntrackedChanges.Clear();

        // Add unstaged files (tracked files with modifications)
        foreach (var f in UnstagedFiles)
        {
            var item = new GitFileChangeItem(f, selectedPaths.Contains(f.Path));
            item.SelectionChanged += OnItemSelectionChanged;
            Changes.Add(item);
            TrackedChanges.Add(item);
        }

        // Add untracked files (new files not yet in version control)
        foreach (var f in UntrackedFiles)
        {
            if (!Changes.Any(c => c.Path == f.Path))
            {
                var item = new GitFileChangeItem(f, selectedPaths.Contains(f.Path));
                item.SelectionChanged += OnItemSelectionChanged;
                Changes.Add(item);
                UntrackedChanges.Add(item);
            }
        }

        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedFilePaths));
    }

    private void OnItemSelectionChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedFilePaths));
    }

    [RelayCommand]
    private void NavigateTo(GitClientPage page)
    {
        ActivePage = page;
        if (page == GitClientPage.Remotes)
            _ = RefreshRemotesAsync();
    }

    // ── 远程（remote）管理 ──

    private bool CanManageRemotes => SelectedRepository is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanManageRemotes))]
    private async Task RefreshRemotesAsync()
    {
        if (SelectedRepository is null) { Log("RefreshRemotesAsync: SelectedRepository=null → 跳过"); return; }
        Log($"RefreshRemotesAsync: 调用 client.ListRemotesAsync(RepoId={SelectedRepository.Id}) …");
        try
        {
            var remotes = await client.ListRemotesAsync(SelectedRepository.Id);
            Remotes.Clear();
            foreach (var r in remotes) Remotes.Add(r);
            Log($"加载远程完成: count={Remotes.Count} items=[{string.Join(",", remotes.Select(r => $"{r.Name}={r.FetchUrl}"))}]");
            if (Remotes.Count > 0) SelectedRemote = Remotes[0];
            else SelectedRemote = null;
        }
        catch (Exception ex)
        {
            await NotifyAsync(LocalizedText.Format("git.vm.load_remotes_failed_format", ex.Message));
            Log($"RefreshRemotesAsync 异常：{ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanManageRemotes))]
    private async Task AddRemoteAsync()
    {
        if (SelectedRepository is null || ShowRemoteDialogAsync is null) return;
        var request = await ShowRemoteDialogAsync(null);
        if (request is null) return;
        try
        {
            var result = await client.AddRemoteAsync(SelectedRepository.Id, request);
            if (result.Success)
                StatusText = LocalizedText.Format("git.vm.add_remote_format", request.Name);
            else
                await NotifyAsync(LocalizedText.Format("git.vm.add_remote_failed_format", result.Message));
            await RefreshRemotesAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.add_remote_failed_format", ex.Message)); }
    }

    [RelayCommand(CanExecute = nameof(CanManageRemotes))]
    private async Task EditRemoteAsync(GitRemoteDto remote)
    {
        if (SelectedRepository is null || remote is null || ShowRemoteDialogAsync is null) return;
        var request = await ShowRemoteDialogAsync(remote);
        if (request is null) return;
        try
        {
            var result = await client.UpdateRemoteAsync(SelectedRepository.Id, remote.Name, request);
            if (result.Success)
                StatusText = LocalizedText.Format("git.vm.update_remote_format", request.Name);
            else
                await NotifyAsync(LocalizedText.Format("git.vm.update_remote_failed_format", result.Message));
            await RefreshRemotesAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.update_remote_failed_format", ex.Message)); }
    }

    [RelayCommand(CanExecute = nameof(CanManageRemotes))]
    private async Task RemoveRemoteAsync(GitRemoteDto remote)
    {
        if (SelectedRepository is null || remote is null) return;
        if (ShowConfirmAsync is not null && !await ShowConfirmAsync(LocalizedText.Format("git.vm.delete_remote_confirm_format", remote.Name)))
            return;
        try
        {
            var result = await client.RemoveRemoteAsync(SelectedRepository.Id, remote.Name);
            if (result.Success)
                StatusText = LocalizedText.Format("git.vm.delete_remote_format", remote.Name);
            else
                await NotifyAsync(LocalizedText.Format("git.vm.delete_remote_failed_format", result.Message));
            await RefreshRemotesAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.delete_remote_failed_format", ex.Message)); }
    }

    // ── 选中提交变化：加载提交详情（含变更文件列表）──
    partial void OnSelectedCommitChanged(GitCommitDto? value)
    {
        _ = LoadCommitDetailAsync(value);
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
            StatusText = LocalizedText.Format("git.vm.load_commit_detail_failed_format", ex.Message);
        }
    }

    // ── Push dialog state management ──

    /// <summary>Prepares the push dialog state by loading ahead commits and setting defaults.
    /// Called before showing the push dialog, or when navigating the dialog.</summary>
    public async Task PreparePushPreviewAsync(string? localBranch = null)
    {
        if (SelectedRepository is null || Status is null) return;

        PushIsLoading = true;
        PushStatusMessage = LocalizedText.Get("git.dialog.push.loading_commits");

        try
        {
            PushCommits.Clear();
            ClearPushFilePreview();
            PushSelectedCommit = null;

            PushLocalBranchName = localBranch ?? Status.Branch;
            var branch = Branches.FirstOrDefault(candidate =>
                !candidate.IsRemote && string.Equals(candidate.Name, PushLocalBranchName, StringComparison.Ordinal));

            var upstream = branch?.Tracking ?? (branch?.IsCurrent == true ? Status.Upstream : null);
            if (!string.IsNullOrWhiteSpace(upstream))
            {
                var parts = upstream.Split('/');
                if (parts.Length >= 2)
                {
                    PushSelectedRemote = parts[0];
                    PushSelectedBranch = string.Join("/", parts.Skip(1));
                }
                else
                {
                    PushSelectedRemote = "origin";
                    PushSelectedBranch = upstream;
                }
            }
            else
            {
                PushSelectedRemote = Remotes.Count > 0 ? Remotes[0].Name : "origin";
                PushSelectedBranch = PushLocalBranchName;
            }

            var aheadCount = branch?.Ahead ?? (string.Equals(PushLocalBranchName, Status.Branch, StringComparison.Ordinal) ? Status.Ahead : 0);
            if (aheadCount > 0)
            {
                var commits = await client.GetLogAsync(SelectedRepository.Id, limit: aheadCount + 50,
                    query: new GitLogQuery(Reference: PushLocalBranchName));
                var ahead = commits.Take(aheadCount).ToList();
                foreach (var c in ahead) PushCommits.Add(c);
            }

            PushStatusMessage = PushCommits.Count == 0
                ? LocalizedText.Get("git.dialog.push.no_commits_ahead")
                : LocalizedText.Format("git.vm.push_n_commits_ahead_format", PushCommits.Count);

            var selectionVersion = BeginPushSelection();
            await LoadAllPushChangesAsync(selectionVersion);

            OnPropertyChanged(nameof(PushBranchLineText));
            OnPropertyChanged(nameof(PushFileCount));
            OnPropertyChanged(nameof(PushCommitCount));
            OnPropertyChanged(nameof(PushHasCommits));
        }
        catch (Exception ex)
        {
            PushStatusMessage = LocalizedText.Format("git.vm.push_dialog_prepare_failed", ex.Message);
            Log($"PreparePushPreviewAsync 异常：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            PushIsLoading = false;
        }
    }

    /// <summary>Loads changed files for a single commit to display in the push dialog.</summary>
    private async Task LoadPushCommitFilesAsync(GitCommitDto? commit, int selectionVersion)
    {
        if (commit is null || SelectedRepository is null) return;
        try
        {
            var detail = await client.GetCommitDetailAsync(SelectedRepository.Id, commit.Sha);
            if (selectionVersion != Volatile.Read(ref _pushSelectionVersion)) return;
            SetPushFilePreview(detail.ChangedFiles);
        }
        catch (Exception ex)
        {
            Log($"LoadPushCommitFilesAsync 异常：{ex.Message}");
        }
    }

    /// <summary>Loads combined file changes for all ahead commits (when user clicks the branch line).</summary>
    private async Task LoadAllPushChangesAsync(int selectionVersion)
    {
        if (SelectedRepository is null || PushCommits.Count == 0) return;

        var allPaths = new HashSet<string>();
        var firstCommitFiles = new List<GitFileChangeDto>();

        foreach (var commit in PushCommits)
        {
            try
            {
                var detail = await client.GetCommitDetailAsync(SelectedRepository.Id, commit.Sha);
                foreach (var f in detail.ChangedFiles)
                {
                    if (allPaths.Add(f.Path))
                        firstCommitFiles.Add(f);
                }
            }
            catch { /* skip failed commits */ }
        }

        if (selectionVersion != Volatile.Read(ref _pushSelectionVersion)) return;
        SetPushFilePreview(firstCommitFiles);
    }

    /// <summary>Called when user selects a commit in the push dialog's left panel.</summary>
    [RelayCommand]
    private async Task SelectPushCommitAsync(GitCommitDto? commit)
    {
        if (commit is null) return;
        PushSelectedCommit = commit;
        var selectionVersion = BeginPushSelection();
        ClearPushFilePreview();
        await LoadPushCommitFilesAsync(commit, selectionVersion);
    }

    /// <summary>Called when user clicks the branch line to view all changes combined.</summary>
    [RelayCommand]
    private async Task SelectAllPushCommitsAsync()
    {
        PushSelectedCommit = null;
        var selectionVersion = BeginPushSelection();
        ClearPushFilePreview();
        await LoadAllPushChangesAsync(selectionVersion);
    }

    partial void OnPushSelectedCommitChanged(GitCommitDto? value)
    {
        OnPropertyChanged(nameof(PushHasSelectedCommit));
        OnPropertyChanged(nameof(PushAllCommitsActive));
    }

    private int BeginPushSelection() => Interlocked.Increment(ref _pushSelectionVersion);

    private void ClearPushFilePreview()
    {
        PushChangedFiles.Clear();
        PushFileTree.Clear();
        OnPropertyChanged(nameof(PushFileCount));
    }

    private void SetPushFilePreview(IEnumerable<GitFileChangeDto> files)
    {
        PushChangedFiles.Clear();
        foreach (var file in files) PushChangedFiles.Add(file);

        PushFileTree.Clear();
        foreach (var file in PushChangedFiles)
        {
            var parts = file.Path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            var current = PushFileTree;
            for (var index = 0; index < parts.Length; index++)
            {
                var isFile = index == parts.Length - 1;
                var node = current.FirstOrDefault(candidate =>
                    candidate.Name == parts[index] && candidate.IsFile == isFile);
                if (node is null)
                {
                    node = new PushFileTreeNode(parts[index], isFile, isFile ? file.Status : null);
                    current.Add(node);
                }
                current = node.Children;
            }
        }

        SortPushFileTree(PushFileTree);
        OnPropertyChanged(nameof(PushFileCount));
    }

    private static void SortPushFileTree(ObservableCollection<PushFileTreeNode> nodes)
    {
        var ordered = nodes.OrderBy(node => node.IsFile).ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        nodes.Clear();
        foreach (var node in ordered)
        {
            SortPushFileTree(node.Children);
            nodes.Add(node);
        }
    }

    /// <summary>Opens the remote/branch picker dialog for the push target.</summary>
    [RelayCommand]
    private async Task SelectPushRemoteBranch(ManagedWindow? owner)
    {
        if (ShowRemoteBranchPickerDialogAsync is null) return;
        var result = await ShowRemoteBranchPickerDialogAsync(owner, PushSelectedRemote, PushSelectedBranch);
        if (result.HasValue)
        {
            PushSelectedRemote = result.Value.Remote;
            PushSelectedBranch = result.Value.Branch;
            OnPropertyChanged(nameof(PushBranchLineText));
        }
    }

    /// <summary>Commits with the intention of pushing afterwards.
    /// Same as CommitAsync but auto-opens the push dialog on success.</summary>
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CommitAndPushAsync()
    {
        if (SelectedRepository is null) return;
        if (SelectedCount == 0)
        {
            await NotifyAsync(LocalizedText.Get("git.status.no_files_selected"));
            return;
        }
        if (string.IsNullOrWhiteSpace(CommitMessage))
        {
            await NotifyAsync(LocalizedText.Get("git.status.commit_message_required"));
            return;
        }

        IsBusy = true;
        try
        {
            var request = new GitCommitRequest(CommitMessage, SelectedFilePaths.ToArray(), false);
            var result = await client.CommitAsync(SelectedRepository.Id, request);
            if (result.Success)
            {
                StatusText = LocalizedText.Get("git.status.committed");
                CommitMessage = string.Empty;
                await RefreshAllAsync();

                await PreparePushPreviewAsync();

                if (ShowPushDialogAsync is not null)
                {
                    await ShowPushDialogAsync();
                }
            }
            else
            {
                await NotifyAsync(LocalizedText.Format("git.status.commit_failed_format", result.Message));
                await RefreshAllAsync();
            }
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    /// <summary>Shows the push dialog for preview/confirmation after a regular commit.
    /// Called from CommitAsync after successful commit.</summary>
    public async Task ShowPushDialogAfterCommitAsync()
    {
        await PreparePushPreviewAsync();
        if (ShowPushDialogAsync is not null)
            await ShowPushDialogAsync();
    }

    /// <summary>Executes the push from the preview dialog. If Git reports missing HTTPS credentials,
    /// prompts within that dialog and retries once with the supplied credentials.</summary>
    public async Task<bool> ExecutePushFromDialogAsync(ManagedWindow? owner)
    {
        if (SelectedRepository is null) return false;
        StatusText = LocalizedText.Get("git.vm.push_progress");
        PushStatusMessage = LocalizedText.Get("git.dialog.push.pushing");
        try
        {
            var pushRequest = new GitPushRequest(
                LocalBranch: PushLocalBranchName,
                Remote: PushSelectedRemote,
                RemoteBranch: PushSelectedBranch);
            var result = await client.PushAsync(SelectedRepository.Id, pushRequest);
            if (result.RequiresCredentials)
            {
                if (ShowGitCredentialsDialogAsync is null) return false;
                var credentials = await ShowGitCredentialsDialogAsync(owner);
                if (credentials is null)
                {
                    PushStatusMessage = LocalizedText.Get("git.dialog.credentials.canceled");
                    return false;
                }
                result = await client.PushAsync(SelectedRepository.Id, pushRequest with
                {
                    Credentials = credentials,
                    SaveCredentials = credentials.SaveCredentials,
                });
            }

            if (result.Success)
            {
                StatusText = LocalizedText.Get("git.vm.pushed");
                PushStatusMessage = LocalizedText.Get("git.vm.pushed");
                await RefreshAllAsync();
                return true;
            }

            if (IsNonFastForwardPushRejection(result) &&
                string.Equals(PushLocalBranchName, Status?.Branch, StringComparison.Ordinal))
            {
                var integration = ShowPushRejectedDialogAsync is null
                    ? null
                    : await ShowPushRejectedDialogAsync(owner);
                if (integration is not null)
                {
                    PushStatusMessage = LocalizedText.Get("git.dialog.push_rejected.updating");
                    var pull = await client.PullAsync(SelectedRepository.Id, integration with
                    {
                        Remote = pushRequest.Remote,
                        Refspec = pushRequest.RemoteBranch,
                    });
                    await RefreshAllAsync();
                    if (pull.Conflicts is { Count: > 0 })
                    {
                        PushStatusMessage = LocalizedText.Get("git.dialog.push_rejected.resolve_conflicts");
                        await PresentConflictResolutionAsync(owner);
                    }
                    else if (pull.Success)
                    {
                        PushStatusMessage = LocalizedText.Get("git.dialog.push_rejected.ready_to_retry");
                    }
                    else
                    {
                        PushStatusMessage = LocalizedText.Format("git.vm.pull_failed_format", pull.Message);
                    }
                    return false;
                }
            }

            PushStatusMessage = result.RequiresCredentials
                ? LocalizedText.Get("git.dialog.credentials.failed")
                : LocalizedText.Format("git.vm.push_failed_format", result.Message);
            return false;
        }
        catch (Exception ex)
        {
            PushStatusMessage = LocalizedText.Format("git.status.error_format", ex.Message);
            return false;
        }
    }

    private static bool IsNonFastForwardPushRejection(GitOperationResult result)
    {
        var message = result.Message ?? string.Empty;
        return !result.Success && !result.RequiresCredentials &&
            (message.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("fetch first", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("[rejected]", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>获取远程分支名称列表（从已加载的 Branches 中过滤 IsRemote=true）。
    /// 如分支列表未加载则触发一次加载。</summary>
    public async Task<IReadOnlyList<string>> GetRemoteBranchNamesAsync()
    {
        if (SelectedRepository is null) return Array.Empty<string>();
        if (Branches.Count == 0)
        {
            try
            {
                var branches = await client.ListBranchesAsync(SelectedRepository.Id);
                Branches.Clear();
                foreach (var b in branches) Branches.Add(b);
            }
            catch { /* silent */ }
        }
        return Branches.Where(b => b.IsRemote).Select(b =>
        {
            // Strip remote prefix (e.g., "origin/master" → "master")
            var slashIdx = b.Name.IndexOf('/');
            return slashIdx >= 0 ? b.Name.Substring(slashIdx + 1) : b.Name;
        }).Distinct().ToList();
    }

    // ── 分支右键菜单命令 ──

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CheckoutBranchAsync(GitBranchDto? branch)
    {
        if (branch is null) branch = SelectedBranch;
        if (branch is null) return;
        await CheckoutAsync(branch);
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CheckoutAndUpdateBranchAsync(GitBranchDto? branch)
    {
        branch ??= SelectedBranch;
        if (branch is null || branch.IsRemote) return;
        await CheckoutAsync(branch);
        // CheckoutAsync refreshes Status only after a successful checkout. Avoid
        // pulling the old branch when checkout was rejected by local changes.
        if (string.Equals(Status?.Branch, branch.Name, StringComparison.Ordinal))
            await PullAsync();
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CreateBranchFromHereAsync(GitBranchDto? baseBranch)
    {
        if (ShowCreateBranchDialogAsync is null || SelectedRepository is null) return;
        var request = await ShowCreateBranchDialogAsync(baseBranch);
        if (request is null) return;
        // 若用户对话框未指定起点，则以右键选中的分支作为起点
        var startPoint = string.IsNullOrWhiteSpace(request.StartPoint) && baseBranch is not null
            ? baseBranch.Name
            : request.StartPoint;
        IsBusy = true;
        try
        {
            var result = await client.CreateBranchAsync(SelectedRepository.Id,
                request with { StartPoint = startPoint });
            if (result.Success)
                StatusText = LocalizedText.Format("git.vm.branch_created_format", request.Name);
            else
                await NotifyAsync(LocalizedText.Format("git.vm.create_failed_format", result.Message));
            await RefreshAllAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.create_failed_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task DeleteBranchContextAsync(GitBranchDto? branch)
    {
        if (branch is null) branch = SelectedBranch;
        if (branch is null) return;
        await DeleteBranchAsync(branch);
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RenameBranchAsync(GitBranchDto? branch)
    {
        branch ??= SelectedBranch;
        if (branch is null || SelectedRepository is null) return;
        if (branch.IsRemote) { await NotifyAsync(LocalizedText.Get("git.vm.rename_remote_branch")); return; }

        // 弹输入框取新名称；对话框未接入壳时走二次确认+占位提示（服务端接口已就绪）
        string? newName = null;
        if (ShowRenameBranchDialogAsync is not null)
            newName = await ShowRenameBranchDialogAsync(branch);
        else if (ShowConfirmAsync is not null)
        {
            // 壳暂未注入重命名输入对话框时，降级为直接尝试一个合理默认行为：追加 "-2" 后缀（便于先跑通接口链路）
            if (!await ShowConfirmAsync(LocalizedText.Format("git.vm.rename_confirm_format", branch.Name)))
                return;
            newName = $"{branch.Name}-2";
        }

        if (string.IsNullOrWhiteSpace(newName)) return;
        IsBusy = true;
        try
        {
            var result = await client.RenameBranchAsync(SelectedRepository.Id, branch.Name,
                new GitBranchRenameRequest(newName));
            if (result.Success)
            {
                StatusText = LocalizedText.Format("git.vm.renamed_format", branch.Name, newName);
                await RefreshAllAsync();
            }
            else
            {
                await NotifyAsync(LocalizedText.Format("git.vm.rename_failed_format", result.Message));
            }
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.rename_failed_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task MergeBranchAsync(GitBranchDto? branch)
    {
        branch ??= SelectedBranch;
        if (branch is null || SelectedRepository is null) return;

        GitMergeRequest? request;
        if (ShowMergeDialogAsync is not null)
        {
            request = await ShowMergeDialogAsync(branch);
            if (request is null) return;
        }
        else
        {
            // 默认策略：普通 merge（非 ff-only / squash），让 git 根据仓库配置决定是否生成合并提交
            request = new GitMergeRequest(branch.Name);
            if (ShowConfirmAsync is not null
                && !await ShowConfirmAsync(LocalizedText.Format("git.vm.merge_confirm_format", branch.Name)))
                return;
        }

        IsBusy = true;
        try
        {
            var result = await client.MergeBranchAsync(SelectedRepository.Id, request);
            if (result.Conflicts is not null && result.Conflicts.Count > 0)
            {
                StatusText = LocalizedText.Format("git.vm.merge_conflicts_format", result.Conflicts.Count);
                HasConflicts = true;
                await RefreshAllAsync();
                await PresentConflictResolutionAsync();
            }
            else if (result.Success)
            {
                StatusText = LocalizedText.Format("git.vm.merged_format", request.Source);
                await RefreshAllAsync();
            }
            else
            {
                await NotifyAsync(LocalizedText.Format("git.vm.merge_failed_format", result.Message));
            }
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.merge_failed_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CompareBranchAsync(GitBranchDto? branch)
    {
        branch ??= SelectedBranch;
        if (branch is null || SelectedRepository is null) return;
        IsBusy = true;
        try
        {
            var comparison = await client.CompareBranchAsync(SelectedRepository.Id, branch.Name);
            // Reuse the log view's changed-files pane for the branch-vs-working-tree
            // result.  Clearing the selected commit first prevents its async detail
            // loader from overwriting the comparison file list.
            SelectedCommit = null;
            CommitChangedFiles.Clear();
            foreach (var file in comparison.ChangedFiles) CommitChangedFiles.Add(file);
            CommitDetail = new GitCommitDetailDto(
                string.Empty,
                string.Empty,
                string.Empty,
                LocalizedText.Format("git.vm.branch_comparison_title_format", comparison.Branch),
                [],
                comparison.ChangedFiles);
            StatusText = LocalizedText.Format("git.vm.branch_comparison_loaded_format", comparison.Branch, comparison.ChangedFiles.Count);
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.branch_comparison_failed_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RebaseBranchAsync(GitBranchDto? branch)
    {
        branch ??= SelectedBranch;
        if (branch is null) return;
        await NotifyAsync(LocalizedText.Format("git.vm.rebase_unimplemented_format", branch.Name));
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task PushBranchAsync(GitBranchDto? branch)
    {
        branch ??= SelectedBranch;
        if (branch is null) return;
        if (branch.IsRemote) { await NotifyAsync(LocalizedText.Get("git.vm.cannot_push_remote")); return; }
        IsBusy = true;
        try
        {
            await PreparePushPreviewAsync(branch.Name);
            if (ShowPushDialogAsync is not null)
                await ShowPushDialogAsync();
            else
                await ExecutePushFromDialogAsync(null);
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task PullBranchAsync(GitBranchDto? branch)
    {
        branch ??= SelectedBranch;
        if (branch is null) return;
        if (!branch.IsRemote)
        {
            GitPullRequest? branchRequest = new();
            if (ShowPullDialogAsync is not null)
                branchRequest = await ShowPullDialogAsync();
            if (branchRequest is null || SelectedRepository is null) return;

            IsBusy = true;
            try
            {
                var result = await client.PullAsync(SelectedRepository.Id, branchRequest with { Branch = branch.Name });
                if (result.Conflicts is { Count: > 0 })
                {
                    await RefreshAllAsync();
                    await PresentConflictResolutionAsync();
                }
                else if (result.Success)
                {
                    StatusText = LocalizedText.Get("git.vm.pulled");
                    await RefreshAllAsync();
                }
                else
                {
                    await NotifyAsync(LocalizedText.Format("git.vm.pull_failed_format", result.Message));
                }
            }
            catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.pull_failed_format", ex.Message)); }
            finally { IsBusy = false; }
            return;
        }

        var slash = branch.Name.IndexOf('/');
        if (slash <= 0 || slash == branch.Name.Length - 1)
        {
            await NotifyAsync(LocalizedText.Format("git.vm.failed_format", "远程分支名称无效。"));
            return;
        }

        GitPullRequest? request = new();
        if (ShowPullDialogAsync is not null)
            request = await ShowPullDialogAsync();
        if (request is null || SelectedRepository is null) return;

        IsBusy = true;
        try
        {
            var result = await client.PullAsync(SelectedRepository.Id,
                request with { Remote = branch.Name[..slash], Refspec = branch.Name[(slash + 1)..] });
            if (result.Success)
            {
                StatusText = LocalizedText.Get("git.vm.pulled");
                await RefreshAllAsync();
            }
            else if (result.Conflicts is not null && result.Conflicts.Count > 0)
            {
                HasConflicts = true;
                await RefreshAllAsync();
                await PresentConflictResolutionAsync();
            }
            else
            {
                await NotifyAsync(LocalizedText.Format("git.vm.pull_failed_format", result.Message));
            }
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.pull_failed_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task FetchBranchAsync(GitBranchDto? _) => await FetchAsync();

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task SetUpstreamBranchAsync(GitBranchDto? branch)
    {
        branch ??= SelectedBranch;
        if (branch is null || SelectedRepository is null) return;
        if (branch.IsRemote) { await NotifyAsync(LocalizedText.Get("git.vm.cannot_set_tracking_remote")); return; }

        GitBranchTrackingRequest? request;
        if (ShowSetTrackingDialogAsync is not null)
        {
            request = await ShowSetTrackingDialogAsync(branch);
            if (request is null) return;
        }
        else
        {
            // 默认：若分支名形式为 "origin/foo" 则尝试匹配远程分支；否则自动绑定 origin/<same-name>
            if (ShowConfirmAsync is not null && !await ShowConfirmAsync(LocalizedText.Format("git.vm.set_tracking_confirm_format", branch.Name)))
                return;
            request = new GitBranchTrackingRequest(Remote: "origin", Branch: branch.Name);
        }

        IsBusy = true;
        try
        {
            var result = await client.SetBranchTrackingAsync(SelectedRepository.Id, branch.Name, request);
            if (result.Success)
            {
                var unset = string.IsNullOrWhiteSpace(request.Upstream)
                            && string.IsNullOrWhiteSpace(request.Remote)
                            && string.IsNullOrWhiteSpace(request.Branch);
                StatusText = unset
                    ? LocalizedText.Format("git.vm.tracking_unset_format", branch.Name)
                    : LocalizedText.Format("git.vm.tracking_set_format", branch.Name,
                        string.IsNullOrWhiteSpace(request.Upstream) ? $"{request.Remote}/{request.Branch}" : request.Upstream);
                await RefreshAllAsync();
            }
            else
            {
                await NotifyAsync(LocalizedText.Format("git.vm.set_tracking_failed_format", result.Message));
            }
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.set_tracking_failed_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    // ── 提交右键菜单命令 ──

    [RelayCommand]
    private async Task CopyShaAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        try
        {
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                var topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
                if (topLevel?.Clipboard is not null)
                {
                    await topLevel.Clipboard.SetTextAsync(commit.Sha);
                    StatusText = LocalizedText.Format("git.vm.copy_sha_format", commit.ShortSha);
                    return;
                }
            }
            // 兜底：通过 Dispatcher + TopLevel 遍历查找
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var tl = Avalonia.Controls.TopLevel.GetTopLevel((Avalonia.Visual?)null);
                if (tl?.Clipboard is not null) await tl.Clipboard.SetTextAsync(commit.Sha);
            });
            StatusText = LocalizedText.Format("git.vm.sha_no_toplevel_format", commit.ShortSha);
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.copy_failed_format", ex.Message)); }
    }

    [RelayCommand]
    private async Task CopyShortShaAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        try
        {
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                var topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
                if (topLevel?.Clipboard is not null)
                {
                    await topLevel.Clipboard.SetTextAsync(commit.ShortSha);
                    StatusText = LocalizedText.Format("git.vm.copied_short_sha_format", commit.ShortSha);
                    return;
                }
            }
            StatusText = LocalizedText.Format("git.vm.short_sha_format", commit.ShortSha);
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.copy_failed_format", ex.Message)); }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CheckoutCommitAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null || SelectedRepository is null) return;
        if (ShowConfirmAsync is not null && !await ShowConfirmAsync(LocalizedText.Format("git.vm.checkout_commit_confirm_format", commit.ShortSha)))
            return;
        IsBusy = true;
        try
        {
            var result = await client.CheckoutAsync(SelectedRepository.Id, new GitCheckoutRequest(commit.Sha));
            if (result.Success)
                StatusText = LocalizedText.Format("git.vm.checked_out_format", commit.ShortSha);
            else
                await NotifyAsync(LocalizedText.Format("git.vm.checkout_failed_format", result.Message));
            await RefreshAllAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.checkout_failed_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task ResetToCommitAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null || SelectedRepository is null) return;
        if (ShowConfirmAsync is not null && !await ShowConfirmAsync(LocalizedText.Format("git.vm.reset_confirm_format", commit.ShortSha)))
            return;
        IsBusy = true;
        try
        {
            // Mixed reset retains working-tree content and is the safest useful reset mode for this UI.
            var result = await client.ResetAsync(SelectedRepository.Id, new GitResetRequest(commit.Sha, "mixed"));
            if (result.Success)
                StatusText = LocalizedText.Format("git.vm.reset_success_format", commit.ShortSha);
            else
                await NotifyAsync(LocalizedText.Format("git.vm.reset_failed_format", result.Message));
            await RefreshAllAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.reset_failed_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RevertCommitContextAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        await RevertAsync(commit);
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task UndoCommitAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null || SelectedRepository is null) return;
        var head = Commits.FirstOrDefault();
        if (head is null || !string.Equals(head.Sha, commit.Sha, StringComparison.OrdinalIgnoreCase))
        {
            await NotifyAsync(LocalizedText.Get("git.vm.undo_only_head"));
            return;
        }
        if (ShowConfirmAsync is not null && !await ShowConfirmAsync(LocalizedText.Format("git.vm.undo_confirm_format", commit.ShortSha)))
            return;
        IsBusy = true;
        try
        {
            // Soft reset removes only HEAD while preserving every file and its staged state.
            var result = await client.ResetAsync(SelectedRepository.Id, new GitResetRequest("HEAD^", "soft"));
            if (result.Success)
                StatusText = LocalizedText.Format("git.vm.undo_success_format", commit.ShortSha);
            else
                await NotifyAsync(LocalizedText.Format("git.vm.undo_failed_format", result.Message));
            await RefreshAllAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.undo_failed_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task CreatePatchFromCommitAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        await NotifyAsync(LocalizedText.Format("git.vm.create_patch_unimplemented_format", commit.ShortSha));
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CherryPickCommitAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        await NotifyAsync(LocalizedText.Format("git.vm.cherry_pick_unimplemented_format", commit.ShortSha));
    }

    [RelayCommand]
    private async Task RebaseInteractiveFromHereAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        await NotifyAsync(LocalizedText.Format("git.vm.rebase_interactive_unimplemented_format", commit.ShortSha));
    }

    [RelayCommand]
    private async Task SquashFromHereAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        await NotifyAsync(LocalizedText.Format("git.vm.squash_unimplemented_format", commit.ShortSha));
    }

    [RelayCommand]
    private async Task EditCommitMessageAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        await NotifyAsync(LocalizedText.Format("git.vm.edit_message_unimplemented_format", commit.ShortSha));
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task PushAllBeforeAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        await NotifyAsync(LocalizedText.Format("git.vm.push_before_unimplemented_format", commit.ShortSha));
    }

    [RelayCommand]
    private async Task CreateTagFromCommitAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null) return;
        await NotifyAsync(LocalizedText.Format("git.vm.create_tag_unimplemented_format", commit.ShortSha));
    }

    [RelayCommand]
    private async Task CreateBranchAtCommitAsync(GitCommitDto? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null || ShowCreateBranchDialogAsync is null || SelectedRepository is null) return;
        var request = await ShowCreateBranchDialogAsync(null);
        if (request is null) return;
        var startPoint = string.IsNullOrWhiteSpace(request.StartPoint) ? commit.Sha : request.StartPoint;
        IsBusy = true;
        try
        {
            var result = await client.CreateBranchAsync(SelectedRepository.Id,
                request with { StartPoint = startPoint });
            if (result.Success)
                StatusText = LocalizedText.Format("git.vm.branch_created_at_format", request.Name, commit.ShortSha);
            else
                await NotifyAsync(LocalizedText.Format("git.vm.create_failed_format", result.Message));
            await RefreshAllAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.create_failed_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    // ── 文件（工作区/提交详情）右键菜单命令 ──

    [RelayCommand]
    private async Task ShowFileDiffContextAsync(GitFileChangeDto? file)
    {
        file ??= SelectedCommitFile ?? SelectedFile;
        if (file is null) return;
        await ViewDiffAsync(file);
    }

    [RelayCommand]
    private async Task ShowCommitFileDiffAsync(GitFileChangeDto? file)
    {
        file ??= SelectedCommitFile;
        if (file is null || SelectedRepository is null || SelectedCommit is null) return;
        try
        {
            // 使用 ref=sha 查询提交版本的 diff
            FileDiff = await client.GetDiffAsync(SelectedRepository.Id, file.Path, staged: false, @ref: SelectedCommit.Sha);
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.diff_failed_format_v2", ex.Message)); }
    }

    [RelayCommand]
    private async Task OpenFileInEditorAsync(GitFileChangeDto? file)
    {
        file ??= SelectedCommitFile ?? SelectedFile;
        if (file is null) return;
        // TODO: 通过 RelaxKonOS 内置 CodeEditor 或宿主 OS 默认编辑器打开，当前占位
        await NotifyAsync(LocalizedText.Format("git.vm.open_file_unimplemented_format", file.Path));
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RevertFileChangeAsync(GitFileChangeDto? file)
    {
        file ??= SelectedFile;
        if (file is null || SelectedRepository is null) return;
        if (ShowConfirmAsync is not null && !await ShowConfirmAsync(LocalizedText.Format("git.vm.revert_file_confirm_format", file.Path)))
            return;
        IsBusy = true;
        try
        {
            var result = await client.RestoreAsync(SelectedRepository.Id, new GitRestoreRequest([file.Path]));
            if (result.Success)
                StatusText = LocalizedText.Format("git.vm.revert_file_success_format", file.Path);
            else
                await NotifyAsync(LocalizedText.Format("git.vm.revert_file_failed_format", result.Message));
            await RefreshAllAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.revert_file_failed_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task StageFileContextAsync(GitFileChangeDto? file)
    {
        file ??= SelectedFile;
        if (file is null) return;
        await StageFileAsync(file);
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task UnstageFileAsync(GitFileChangeDto? file)
    {
        file ??= SelectedFile;
        if (file is null || SelectedRepository is null) return;
        IsBusy = true;
        try
        {
            var result = await client.UnstageAsync(SelectedRepository.Id, new GitUnstageRequest([file.Path]));
            if (result.Success)
                StatusText = LocalizedText.Format("git.vm.unstaged_format", file.Path);
            else
                await NotifyAsync(LocalizedText.Format("git.vm.unstage_failed_format", result.Message));
            await RefreshAllAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.unstage_failed_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ShowFileHistoryAsync(GitFileChangeDto? file)
    {
        file ??= SelectedCommitFile ?? SelectedFile;
        if (file is null) return;
        await NotifyAsync(LocalizedText.Format("git.vm.file_history_unimplemented_format", file.Path));
    }

    [RelayCommand]
    private async Task CreatePatchFromFileAsync(GitFileChangeDto? file)
    {
        file ??= SelectedCommitFile ?? SelectedFile;
        if (file is null) return;
        await NotifyAsync(LocalizedText.Format("git.vm.create_patch_file_unimplemented_format", file.Path));
    }
}
