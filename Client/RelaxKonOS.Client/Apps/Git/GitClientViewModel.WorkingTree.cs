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
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CheckoutAsync(GitBranchDto branch)
    {
        if (SelectedRepository is null || branch is null) return;
        IsBusy = true;
        StatusText = LocalizedText.Ref("git.vm.checkout_progress_format", branch.Name);
        try
        {
            var result = await client.CheckoutAsync(SelectedRepository.Id, new GitCheckoutRequest(branch.Name));
            if (result.Success)
            {
                StatusText = LocalizedText.Ref("git.vm.switched_to_format", branch.Name);
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
                StatusText = LocalizedText.Ref("git.vm.branch_created_format", request.Name);
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
                StatusText = LocalizedText.Ref("git.vm.branch_deleted_format", branch.Name);
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
                    StatusText = LocalizedText.Ref("git.status.committed");
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
                StatusText = LocalizedText.Ref("git.status.committed");
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
        StatusText = LocalizedText.Ref("git.vm.pull_progress");
        try
        {
            var result = await client.PullAsync(SelectedRepository.Id, request);
            if (result.Conflicts is { Count: > 0 })
            {
                await RefreshAllAsync();
                StatusText = LocalizedText.Ref("git.vm.merge_conflicts_format", result.Conflicts.Count);
                await PresentConflictResolutionAsync();
            }
            else if (result.RequiresCredentials)
                await NotifyAsync(LocalizedText.Get("git.vm.credentials_required"));
            else if (result.Success)
            {
                await RefreshAllAsync();
                StatusText = LocalizedText.Ref("git.vm.pulled");
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
                StatusText = LocalizedText.Ref("git.vm.fetched");
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
        => await ShowWorkingTreeFileDiffAsync(file, null);

    /// <summary>Loads a worktree/index diff and opens it relative to the supplied modal owner.</summary>
    public async Task ShowWorkingTreeFileDiffAsync(GitFileChangeDto file, ManagedWindow? owner)
    {
        if (SelectedRepository is null || file is null) return;
        try
        {
            FileDiff = await client.GetDiffAsync(SelectedRepository.Id, file.Path, file.Staged);
            if (ShowFileDiffAsync is not null)
                await ShowFileDiffAsync(owner, FileDiff);
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
                StatusText = LocalizedText.Ref("git.vm.reverted");
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
                StatusText = LocalizedText.Ref("git.vm.staged_format", file.Path);
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
        StatusText = LocalizedText.Ref("git.status.refreshed_changes", Changes.Count);
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

}
