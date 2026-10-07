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
                    StatusText = LocalizedText.Ref("git.vm.copy_sha_format", commit.ShortSha);
                    return;
                }
            }
            // 兜底：通过 Dispatcher + TopLevel 遍历查找
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var tl = Avalonia.Controls.TopLevel.GetTopLevel((Avalonia.Visual?)null);
                if (tl?.Clipboard is not null) await tl.Clipboard.SetTextAsync(commit.Sha);
            });
            StatusText = LocalizedText.Ref("git.vm.sha_no_toplevel_format", commit.ShortSha);
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
                    StatusText = LocalizedText.Ref("git.vm.copied_short_sha_format", commit.ShortSha);
                    return;
                }
            }
            StatusText = LocalizedText.Ref("git.vm.short_sha_format", commit.ShortSha);
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
                StatusText = LocalizedText.Ref("git.vm.checked_out_format", commit.ShortSha);
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
                StatusText = LocalizedText.Ref("git.vm.reset_success_format", commit.ShortSha);
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
                StatusText = LocalizedText.Ref("git.vm.undo_success_format", commit.ShortSha);
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
                StatusText = LocalizedText.Ref("git.vm.branch_created_at_format", request.Name, commit.ShortSha);
            else
                await NotifyAsync(LocalizedText.Format("git.vm.create_failed_format", result.Message));
            await RefreshAllAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.create_failed_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    // ── 文件（工作区/提交详情）右键菜单命令 ──

}
