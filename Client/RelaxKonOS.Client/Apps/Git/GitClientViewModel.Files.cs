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
        if (file is null) return;
        // The log page has room for the diff beside the commit list, so this path does not open a
        // window; the worktree page still uses the modal shell for its own file list.
        await ShowInlineCommitFileDiffAsync(file);
    }

    /// <summary>Opens the selected push commit's file diff, or the originating commit for an aggregate preview entry.</summary>
    public async Task ShowPushFileDiffAsync(GitFileChangeDto file, ManagedWindow? owner, string? commitSha)
    {
        commitSha ??= PushSelectedCommit?.Sha;
        if (commitSha is null)
        {
            await ShowWorkingTreeFileDiffAsync(file, owner);
            return;
        }

        if (SelectedRepository is null) return;
        try
        {
            FileDiff = await client.GetDiffAsync(SelectedRepository.Id, file.Path, staged: false, @ref: commitSha);
            if (ShowFileDiffAsync is not null)
                await ShowFileDiffAsync(owner, FileDiff);
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.vm.diff_failed_format", ex.Message)); }
    }

    [RelayCommand]
    private async Task OpenFileInEditorAsync(GitFileChangeDto? file)
    {
        file ??= SelectedCommitFile ?? SelectedFile;
        if (file is null) return;
        if (OpenEditorFileAsync is not null) await OpenEditorFileAsync(file);
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RevertFileChangeAsync(GitFileChangeDto? file)
    {
        file ??= SelectedFile;
        if (file is null || SelectedRepository is null) return;
        if (ShowConfirmAsync is not null && !await ShowConfirmAsync(LocalizedText.Format("git.vm.revert_file_confirm_format", file.Path)))
            return;
        if (!await PrepareEditorAsync()) return;
        IsBusy = true;
        try
        {
            var result = await client.RestoreAsync(SelectedRepository.Id, new GitRestoreRequest([file.Path]));
            if (result.Success)
                StatusText = LocalizedText.Ref("git.vm.revert_file_success_format", file.Path);
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
        if (!await PrepareEditorAsync()) return;
        IsBusy = true;
        try
        {
            var result = await client.UnstageAsync(SelectedRepository.Id, new GitUnstageRequest([file.Path]));
            if (result.Success)
                StatusText = LocalizedText.Ref("git.vm.unstaged_format", file.Path);
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
