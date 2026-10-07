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
                StatusText = LocalizedText.Ref("git.vm.branch_created_format", request.Name);
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
                StatusText = LocalizedText.Ref("git.vm.renamed_format", branch.Name, newName);
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
                StatusText = LocalizedText.Ref("git.vm.merge_conflicts_format", result.Conflicts.Count);
                HasConflicts = true;
                await RefreshAllAsync();
                await PresentConflictResolutionAsync();
            }
            else if (result.Success)
            {
                StatusText = LocalizedText.Ref("git.vm.merged_format", request.Source);
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
            StatusText = LocalizedText.Ref("git.vm.branch_comparison_loaded_format", comparison.Branch, comparison.ChangedFiles.Count);
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
                    StatusText = LocalizedText.Ref("git.vm.pulled");
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
                StatusText = LocalizedText.Ref("git.vm.pulled");
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

}
