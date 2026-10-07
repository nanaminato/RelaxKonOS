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

public sealed partial class GitClientViewModel
{
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
        var fileCommitReferences = new Dictionary<string, string>(StringComparer.Ordinal);
        var firstCommitFiles = new List<GitFileChangeDto>();

        foreach (var commit in PushCommits)
        {
            try
            {
                var detail = await client.GetCommitDetailAsync(SelectedRepository.Id, commit.Sha);
                foreach (var f in detail.ChangedFiles)
                {
                    if (allPaths.Add(f.Path))
                    {
                        firstCommitFiles.Add(f);
                        // In aggregate mode, show the first listed (most recent) commit
                        // which changed the file instead of comparing an already-committed
                        // change to the current worktree.
                        fileCommitReferences[f.Path] = commit.Sha;
                    }
                }
            }
            catch { /* skip failed commits */ }
        }

        if (selectionVersion != Volatile.Read(ref _pushSelectionVersion)) return;
        SetPushFilePreview(firstCommitFiles, fileCommitReferences);
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

    private void SetPushFilePreview(IEnumerable<GitFileChangeDto> files, IReadOnlyDictionary<string, string>? fileCommitReferences = null)
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
                    node = new PushFileTreeNode(parts[index], isFile, isFile ? file.Status : null, isFile ? file : null,
                        isFile && fileCommitReferences?.TryGetValue(file.Path, out var commitSha) == true ? commitSha : null);
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
                StatusText = LocalizedText.Ref("git.status.committed");
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
        StatusText = LocalizedText.Ref("git.vm.push_progress");
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
                StatusText = LocalizedText.Ref("git.vm.pushed");
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

}
