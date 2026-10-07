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
                StatusText = LocalizedText.Ref("git.vm.add_remote_format", request.Name);
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
                StatusText = LocalizedText.Ref("git.vm.update_remote_format", request.Name);
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
                StatusText = LocalizedText.Ref("git.vm.delete_remote_format", remote.Name);
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

}
